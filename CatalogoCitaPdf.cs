using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ConsultorAcademicoGui
{
    // Un registro de biblioteca.json de CitaPDF (DocumentoRecord en
    // CitaPDF/Models.cs). Sólo los campos que muestra la ficha.
    public class FichaCitaPdf
    {
        public string DocumentoId { get; set; } = "";
        public string Titulo { get; set; } = "";
        public List<string> AutoresApa { get; set; } = new();
        public string Anio { get; set; } = "";
        public string Editorial { get; set; } = "";
        public string? OrigenUrl { get; set; }
        public string? RutaArchivoOriginal { get; set; }
        public string HashSha256 { get; set; } = "";
        public DateTime FechaAdquisicion { get; set; }
        public string CitaApa { get; set; } = "";

        public string Autores => string.Join("; ", AutoresApa);
    }

    public class CatalogoLeido
    {
        public string Ruta { get; init; } = "";
        public List<FichaCitaPdf> Fichas { get; init; } = new();
        public Dictionary<string, FichaCitaPdf> PorHash { get; init; } = new();
        // null si se leyó bien. Un catálogo con error se trata como vacío:
        // la ficha es un agregado y nunca impide consultar.
        public string? Error { get; init; }
        public bool Disponible => Error == null;
    }

    // Lectura del catálogo de CitaPDF. CitaPDF es dueño de biblioteca.json y
    // lo reescribe entero desde memoria en cada alta: cualquier cosa que el
    // consultor escribiera ahí se perdería en el próximo guardado de
    // CitaPDF, o lo pisaría. Por eso acá sólo se lee, y abriéndolo con
    // FileShare.ReadWrite | Delete para no trabar a CitaPDF si está abierto.
    //
    // Contrato en tiempo de ejecución: el consultor entiende el formato
    // "Version": 1 de CitaPDF. Con otra versión no se usa el catálogo y se
    // dice por qué, en vez de mostrar fichas mal leídas.
    public static class CatalogoCitaPdf
    {
        public const int VersionSoportada = 1;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private static readonly object Cerrojo = new();
        private static (string Ruta, DateTime Escritura, long Largo)? _claveCache;
        private static CatalogoLeido? _cache;

        // Ubicación por defecto de CitaPDF (Biblioteca.DocumentosDir) cuando
        // no corre en modo portable.
        public static string RutaPorDefecto => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CitaPDF", "biblioteca.json");

        public static string RutaEfectiva(string? configurada) =>
            string.IsNullOrWhiteSpace(configurada) ? RutaPorDefecto : configurada.Trim();

        // Relee sólo si el archivo cambió desde la última lectura.
        public static CatalogoLeido Cargar(string? rutaConfigurada)
        {
            string ruta = RutaEfectiva(rutaConfigurada);
            lock (Cerrojo)
            {
                FileInfo fi;
                try
                {
                    fi = new FileInfo(ruta);
                    if (!fi.Exists)
                        return Guardar(null, new CatalogoLeido { Ruta = ruta, Error = $"No existe el catálogo de CitaPDF: {ruta}" });
                }
                catch (Exception ex)
                {
                    return Guardar(null, new CatalogoLeido { Ruta = ruta, Error = $"Ruta del catálogo inválida ({ex.Message}): {ruta}" });
                }

                var clave = (ruta, fi.LastWriteTimeUtc, fi.Length);
                if (_cache != null && _claveCache == clave) return _cache;
                return Guardar(clave, Leer(ruta));
            }
        }

        private static CatalogoLeido Guardar((string, DateTime, long)? clave, CatalogoLeido leido)
        {
            _claveCache = clave;
            _cache = leido;
            return leido;
        }

        private static CatalogoLeido Leer(string ruta)
        {
            byte[] datos;
            try
            {
                datos = LeerCompartido(ruta);
            }
            catch (Exception ex)
            {
                return new CatalogoLeido { Ruta = ruta, Error = $"No se pudo leer el catálogo de CitaPDF ({ex.Message})." };
            }

            try
            {
                // CitaPDF lo escribe en UTF-8 con BOM, que JsonDocument no acepta.
                ReadOnlyMemory<byte> utf8 = datos;
                if (datos.Length >= 3 && datos[0] == 0xEF && datos[1] == 0xBB && datos[2] == 0xBF)
                    utf8 = utf8[3..];
                using var json = JsonDocument.Parse(utf8);
                var raiz = json.RootElement;
                if (raiz.ValueKind != JsonValueKind.Object ||
                    !raiz.TryGetProperty("Documentos", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    return new CatalogoLeido { Ruta = ruta, Error = $"{ruta} no es una biblioteca de CitaPDF (no tiene \"Documentos\")." };

                if (!raiz.TryGetProperty("Version", out var v) || v.ValueKind != JsonValueKind.Number ||
                    !v.TryGetInt32(out int version) || version != VersionSoportada)
                {
                    string encontrada = raiz.TryGetProperty("Version", out var v2) ? v2.GetRawText() : "sin indicar";
                    return new CatalogoLeido
                    {
                        Ruta = ruta,
                        Error = $"El catálogo de CitaPDF tiene formato versión {encontrada} y este consultor lee la versión " +
                                $"{VersionSoportada}. No se usa hasta actualizar el consultor.",
                    };
                }

                var fichas = docs.Deserialize<List<FichaCitaPdf>>(JsonOpts) ?? new List<FichaCitaPdf>();
                var porHash = new Dictionary<string, FichaCitaPdf>();
                foreach (var f in fichas)
                {
                    f.HashSha256 = (f.HashSha256 ?? "").Trim().ToLowerInvariant();
                    if (f.HashSha256.Length > 0) porHash.TryAdd(f.HashSha256, f);
                }
                return new CatalogoLeido { Ruta = ruta, Fichas = fichas, PorHash = porHash };
            }
            catch (JsonException ex)
            {
                return new CatalogoLeido { Ruta = ruta, Error = $"El catálogo de CitaPDF no se pudo interpretar ({ex.Message})." };
            }
        }

        // CitaPDF guarda con .tmp + File.Move(overwrite): en ese instante el
        // archivo puede no estar. Se reintenta un par de veces antes de dar
        // error.
        private static byte[] LeerCompartido(string ruta)
        {
            for (int intento = 1; ; intento++)
            {
                try
                {
                    using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException) when (intento < 3)
                {
                    Thread.Sleep(150);
                }
            }
        }

        public enum Vinculo { Ninguno, MismoPdf, Manual, ManualNoEncontrado }

        // La ficha de un documento del consultor: primero el vínculo elegido a
        // mano (es una decisión explícita), y si no hay, la del mismo PDF
        // (mismo SHA-256).
        public static (FichaCitaPdf? Ficha, Vinculo Tipo) FichaDe(CheckableItem doc, CatalogoLeido catalogo)
        {
            if (!string.IsNullOrEmpty(doc.FichaVinculada))
            {
                return catalogo.PorHash.TryGetValue(doc.FichaVinculada, out var f)
                    ? (f, Vinculo.Manual)
                    : (null, catalogo.Disponible ? Vinculo.ManualNoEncontrado : Vinculo.Manual);
            }
            if (!string.IsNullOrEmpty(doc.Hash) && catalogo.PorHash.TryGetValue(doc.Hash.ToLowerInvariant(), out var g))
                return (g, Vinculo.MismoPdf);
            return (null, Vinculo.Ninguno);
        }

        // Para buscar sin importar mayúsculas ni tildes.
        public static string Normalizar(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
    }
}
