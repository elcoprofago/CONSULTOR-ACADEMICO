using System.IO;
using System.Security.Cryptography;

namespace ConsultorAcademicoGui
{
    // PDFs de la biblioteca que cambiaron de lugar. El consultor no guarda
    // copia de los PDFs: recuerda la ruta del original y su SHA-256 (el
    // contenido), con el que se lo reconoce aunque se lo haya movido o
    // renombrado. Adaptado de CitaPDF (Servicios/Ubicador.cs); desde la copia
    // es código de este repo.
    public static class Ubicador
    {
        // Sin PDF en la ruta registrada: o se movió, o es un documento
        // agregado antes de que el consultor dejara de copiar PDFs (sin ruta).
        public static bool FaltaArchivo(CheckableItem d) =>
            string.IsNullOrWhiteSpace(d.RutaArchivo) || !File.Exists(d.RutaArchivo);

        // Mismo formato que hashlib.sha256(...).hexdigest() de la ingesta.
        public static string CalcularHash(string ruta)
        {
            using var fs = File.OpenRead(ruta);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        // Carpeta existente más cercana a la ruta vieja, para abrir los
        // diálogos de elegir archivo/carpeta ahí. null si no queda ninguna.
        public static string? CarpetaExistenteMasCercana(string? ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta)) return null;
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(ruta));
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception) { return null; }
        }

        // Recorre 'carpeta' y sus subcarpetas buscando PDFs con alguno de los
        // hashes pedidos (hash -> nombre de archivo anterior, "" si no se
        // conoce). Devuelve hash -> ruta encontrada. Revisa primero los que se
        // siguen llamando igual (lo más común: sólo se movieron) y termina
        // apenas encontró todos.
        public static Dictionary<string, string> Buscar(string carpeta, IReadOnlyDictionary<string, string> buscados,
            IProgress<string>? progreso, CancellationToken ct)
        {
            var opciones = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                // Sin entrar en junctions ni enlaces simbólicos: pueden formar
                // ciclos y recorrer el mismo árbol una y otra vez. Los ocultos
                // sí se revisan (el valor por defecto los saltea).
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            };

            var archivos = new List<string>();
            foreach (var a in Directory.EnumerateFiles(carpeta, "*.pdf", opciones))
            {
                ct.ThrowIfCancellationRequested();
                archivos.Add(a);
                if (archivos.Count % 200 == 0) progreso?.Report($"Listando PDFs... {archivos.Count}");
            }

            var nombres = new HashSet<string>(buscados.Values.Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
            var ordenados = archivos.OrderBy(a => nombres.Contains(Path.GetFileName(a)) ? 0 : 1).ToList();

            var encontrados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ordenados.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string a = ordenados[i];
                progreso?.Report($"Revisando {i + 1} de {ordenados.Count}: {Path.GetFileName(a)}");
                string h;
                try { h = CalcularHash(a); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (buscados.ContainsKey(h) && encontrados.TryAdd(h, a) && encontrados.Count == buscados.Count) break;
            }
            return encontrados;
        }
    }
}
