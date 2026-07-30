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

        // Últimas búsquedas (más reciente primero, tope 10; ver
        // RegistrarBusquedaEnHistorial en MainWindow.xaml.cs).
        public List<string> HistorialBusquedas { get; set; } = new();
    }
}
