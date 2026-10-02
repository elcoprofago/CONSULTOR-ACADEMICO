using System.Text.Json.Serialization;

namespace ConsultorAcademicoGui
{
    public class ConsultaRequest
    {
        public string pregunta { get; set; } = "";
        // documento_ids -- a diferencia de CONSULTOR-GUI no hay "coleccion"
        // (una sola biblioteca, sin selector CSJN/PGN).
        public List<string>? filtro { get; set; }
        public int? k { get; set; }
    }

    public class FragmentoDto
    {
        public string documento_id { get; set; } = "";
        public string titulo { get; set; } = "";
        public string autor { get; set; } = "";
        public string anio { get; set; } = "";
        public int pagina_inicio { get; set; }
        public int pagina_fin { get; set; }
        public string texto { get; set; } = "";
        public string cita { get; set; } = "";
    }

    public class ConsultaResponseDto
    {
        public string rama { get; set; } = "";
        public string? respuesta { get; set; }
        public string? mensaje { get; set; }
        public List<FragmentoDto> fragmentos { get; set; } = new();
        public string? error_llm { get; set; }
        public List<string>? citas_contexto { get; set; }
    }

    // Respuesta de GET /documentos (reemplaza TomosAniosResponse -- acá no
    // hay tomos/años sueltos, sino un documento por entrada de biblioteca).
    public class DocumentoDto
    {
        public string documento_id { get; set; } = "";
        public string titulo { get; set; } = "";
        public string autor { get; set; } = "";
        public string anio { get; set; } = "";
        public string fuente_editorial { get; set; } = "";
        public int n_chunks { get; set; }
        // Ruta del PDF original: el consultor no guarda copia. Null en los
        // documentos anteriores a ese cambio que todavía no se ubicaron; para
        // ésos, copia_interna es la copia vieja de ACADEMICO-PDF si existe.
        public string? ruta_archivo { get; set; }
        public string? origen_url { get; set; }
        public string hash_sha256 { get; set; } = "";
        public string? copia_interna { get; set; }
        // Vínculo elegido a mano con un registro de CitaPDF (su HashSha256).
        public string? ficha_citapdf { get; set; }
    }

    // Cuerpo de POST /documentos/{id}/ruta.
    public class RutaRequest
    {
        public string ruta_archivo { get; set; } = "";
    }

    // Cuerpo de POST /documentos/{id}/ficha (null quita el vínculo).
    public class FichaRequest
    {
        public string? ficha_citapdf { get; set; }
    }

    // --- Unificar con otra copia del consultor (POST /unificar/*) ---------

    public class UnificarRequest
    {
        public string otra { get; set; } = "";  // documentos.json de la otra copia
        public string? biblioteca_otra { get; set; }  // para resolver sus rutas relativas
        public List<string> elegir_otra { get; set; } = new();  // conflictos resueltos con la otra
        public List<string> excluir { get; set; } = new();  // agregados que no se suman
    }

    public class InfoBaseDto
    {
        public string ruta { get; set; } = "";
        public string? modificado { get; set; }
        public long bytes { get; set; }
        public int documentos { get; set; }
        public int fragmentos { get; set; }
    }

    public class VistaDocDto
    {
        public string documento_id { get; set; } = "";
        public string titulo { get; set; } = "";
        public string autor { get; set; } = "";
        public string anio { get; set; } = "";
        public string? fecha_modificacion { get; set; }
        public string? fecha_ingesta { get; set; }
        public int datos { get; set; }
        // Sólo en los agregados:
        public int n_fragmentos { get; set; }
        public string? motivo_no_agregable { get; set; }
    }

    public class CampoConflictoDto
    {
        public string campo { get; set; } = "";
        public string local { get; set; } = "";
        public string otro { get; set; } = "";
    }

    public class ConflictoDto
    {
        public VistaDocDto local { get; set; } = new();
        public VistaDocDto otro { get; set; } = new();
        public List<CampoConflictoDto> campos { get; set; } = new();
        public bool sugerencia_es_otro { get; set; }
    }

    public class CompletadoDto
    {
        public VistaDocDto local { get; set; } = new();
        public VistaDocDto otro { get; set; } = new();
        public List<string> campos { get; set; } = new();
    }

    public class AnalisisUnificacionDto
    {
        public InfoBaseDto esta { get; set; } = new();
        public InfoBaseDto otra { get; set; } = new();
        public int identicos { get; set; }
        public int solo_locales { get; set; }
        public List<ConflictoDto> conflictos { get; set; } = new();
        public List<CompletadoDto> completados { get; set; } = new();
        public List<VistaDocDto> agregados { get; set; } = new();
    }

    public class ResultadoUnificacionDto
    {
        public int agregados { get; set; }
        public int completados { get; set; }
        public int conflictos_con_otra { get; set; }
        public int total { get; set; }
        public string informe { get; set; } = "";
    }

    // Respuesta de POST /ingestar.
    public class IngestaResponseDto
    {
        public string documento_id { get; set; } = "";
        public string titulo { get; set; } = "";
        public string autor { get; set; } = "";
        public string anio { get; set; } = "";
        public int n_paginas { get; set; }
        public int n_chunks { get; set; }
        public List<string> advertencias { get; set; } = new();
        public string ruta_archivo { get; set; } = "";
    }

    // Detalle de un 409 de POST /ingestar (documento duplicado).
    public class IngestaDuplicadoDto
    {
        public string mensaje { get; set; } = "";
        public string documento_id_existente { get; set; } = "";
    }

    // Wrappers del cuerpo de error de FastAPI: HTTPException(status, detail)
    // siempre serializa como {"detail": detail} -- string para 400/500,
    // objeto para 409 (ver POST /ingestar en BACKEND/main.py).
    public class ErrorDetalleSimple
    {
        public string? detail { get; set; }
    }

    public class ErrorDetalleDuplicado
    {
        public IngestaDuplicadoDto detail { get; set; } = new();
    }

    // Ítem del desplegable múltiple de documentos (Requisito 18): Value es
    // el documento_id que viaja en ConsultaRequest.filtro, Label es lo que
    // ve el usuario ("{titulo} — {autor} ({anio})"). Los campos editoriales
    // (Titulo/Autor/Anio/FuenteEditorial) viajan también acá -- sin volver a
    // pedirlos al backend -- para poder armar la cita APA del botón de
    // "copiar cita" de cada fila sin una llamada extra.
    public class CheckableItem
    {
        public string Value { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsChecked { get; set; }
        public string Titulo { get; set; } = "";
        public string Autor { get; set; } = "";
        public string Anio { get; set; } = "";
        public string FuenteEditorial { get; set; } = "";
        // Para el botón "PDF" de cada fila (ver FiltroAbrirPdf_Click).
        public string? RutaArchivo { get; set; }
        public string? CopiaInterna { get; set; }
        public string Hash { get; set; } = "";
        // Para el botón "Ficha" (ver CatalogoCitaPdf.FichaDe).
        public string? FichaVinculada { get; set; }
    }

    // --- Sesión (Guardar/Cargar sesión, Requisitos 21-23) -----------------

    public class SessionEntry
    {
        public DateTime Timestamp { get; set; }
        public string Pregunta { get; set; } = "";
        public List<string> Filtro { get; set; } = new();
        public int K { get; set; }
        public string Rama { get; set; } = "";
        public string? Respuesta { get; set; }
        public string? Mensaje { get; set; }
        public string? ErrorLlm { get; set; }
        public List<FragmentoDto> Fragmentos { get; set; } = new();
    }

    public class SessionFile
    {
        public int Version { get; set; } = 1;
        public DateTime GuardadaEl { get; set; } = DateTime.Now;
        public List<SessionEntry> Historial { get; set; } = new();
    }

    // --- Configuración (botón de engranaje) ---------------------------------

    public class AppSettings
    {
        public int Version { get; set; } = 1;

        // "Claro" | "Oscuro" | "Textura"
        public string Tema { get; set; } = "Oscuro";
        public string? RutaTextura { get; set; }

        // "docx" | "pdf" | "txt"
        public string FormatoExportacion { get; set; } = "docx";
        public string? CarpetaExportacionDefault { get; set; }

        // Ver nota en MainWindow.xaml.cs (IniciarBackendAsync) sobre el
        // alcance real de esta opción: sólo mueve el embedding de la consulta
        // a GPU, no la búsqueda FAISS en sí (faiss-cpu, sin soporte GPU).
        public bool UsarGpuBusqueda { get; set; } = false;

        public double TamanoFuenteResultados { get; set; } = 13.0;

        public int? KResultadosDefault { get; set; }
        public double MmrLambdaDefault { get; set; } = 0.6;

        public bool AutoPurgarAlCerrar { get; set; } = false;

        // Donde se guarda un PDF agregado por URL (ése pasa a ser su único
        // ejemplar: el consultor no copia PDFs). Sin carpeta elegida, agregar
        // por URL se rechaza.
        public string? CarpetaDescargas { get; set; }

        // biblioteca.json de CitaPDF, que el consultor sólo lee. null = la
        // ubicación por defecto de CitaPDF (Documentos\CitaPDF); hace falta
        // elegirla para la versión portable, que guarda sus datos junto al
        // .exe.
        public string? RutaCatalogoCitaPdf { get; set; }

        // Carpeta raíz de los PDF (ver Entorno.CarpetaBiblioteca). null = en
        // modo portable, <unidad>\BASE si existe; si no, ninguna.
        public string? CarpetaBiblioteca { get; set; }

        // Carpeta donde buscar primero los modelos (ver
        // Entorno.CarpetasModelos). null = sólo las de siempre.
        public string? CarpetaModelos { get; set; }

        // Últimas búsquedas (más reciente primero, tope 10; ver
        // RegistrarBusquedaEnHistorial en MainWindow.xaml.cs).
        public List<string> HistorialBusquedas { get; set; } = new();

        // Log en ventana aparte (botón "Desacoplar"): si quedó así al cerrar
        // la app y dónde estaba esa ventana, para reabrirla en el mismo
        // monitor.
        public bool LogDesacoplado { get; set; } = false;
        public PosicionVentana? VentanaLog { get; set; }
    }

    public class PosicionVentana
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximizada { get; set; }
    }
}
