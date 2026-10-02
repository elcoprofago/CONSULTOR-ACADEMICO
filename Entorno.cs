using System.IO;

namespace ConsultorAcademicoGui
{
    // Dónde está cada pieza que la GUI necesita (Python, backend, llama-server,
    // modelos, datos). Dos modos:
    //
    // - Portable: la GUI corre desde app\ de la carpeta portable, que tiene
    //   portable.flag en su raíz (la arma build_portable.py). Todo es relativo
    //   a esa raíz, así que funciona igual en la PC y en el pendrive, con
    //   cualquier letra de unidad:
    //
    //     <raíz>\app\        esta GUI, backend\ y scripts\
    //     <raíz>\runtime\    Python propio
    //     <raíz>\bin\        llama-server y pdftotext
    //     <raíz>\data\       configuración, índice y sesiones de esta copia
    //     <raíz>\Models\     modelos (opcional)
    //     <unidad>\MODELS\   modelos compartidos (los de CodeAgent están ahí)
    //     <unidad>\BASE\     biblioteca de PDF por defecto
    //
    // - Desarrollo (sin portable.flag): las rutas de siempre en esta PC.
    public static class Entorno
    {
        public const string ModeloLlm = "Qwen3.5-9B-Q4_K_M.gguf";
        public const string ModeloEmbeddings = "multilingual-e5-large";

        // Rutas del modo desarrollo.
        private const string DevPython = @"F:\INFO\DERECHO\Jurisprudencia\C.S.J.N\CSJN-PROYECTO\CSJN-SCRIPTS\venv\Scripts\python.exe";
        private const string DevBackendDir = @"F:\INFO\DERECHO\Jurisprudencia\C.S.J.N\CONSULTOR-ACADEMICO-GUI\BACKEND";
        private const string DevLlamaServer = @"E:\llama-server\llama-server.exe";
        private const string DevModelos = @"E:\Models";

        public static readonly string AppDir = AppContext.BaseDirectory.TrimEnd('\\');

        // null fuera del modo portable.
        public static readonly string? RaizPortable = BuscarRaizPortable();

        public static bool EsPortable => RaizPortable != null;

        private static string? BuscarRaizPortable()
        {
            string? raiz = Path.GetDirectoryName(AppDir);
            return raiz != null && File.Exists(Path.Combine(raiz, "portable.flag")) ? raiz : null;
        }

        // Raíz de la unidad desde la que corre (G:\ en el pendrive).
        public static string? RaizUnidad => RaizPortable == null ? null : Path.GetPathRoot(RaizPortable);

        public static string Python => EsPortable ? Path.Combine(RaizPortable!, "runtime", "python.exe") : DevPython;

        public static string BackendDir => EsPortable ? Path.Combine(AppDir, "backend") : DevBackendDir;

        // null = main.py usa ACADEMICO-SCRIPTS del repo vecino.
        public static string? ScriptsDir => EsPortable ? Path.Combine(AppDir, "scripts") : null;

        public static string LlamaServer => EsPortable ? Path.Combine(RaizPortable!, "bin", "llama-server.exe") : DevLlamaServer;

        // null = config.py usa el de CSJN-SCRIPTS.
        public static string? Pdftotext => EsPortable ? Path.Combine(RaizPortable!, "bin", "pdftotext.exe") : null;

        // Configuración, sesiones e índice de esta copia.
        public static string DataDir => EsPortable
            ? Path.Combine(RaizPortable!, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CONSULTOR-ACADEMICO-GUI");

        // null = config.py usa ACADEMICO-PROYECTO (el índice de siempre).
        public static string? IndiceDir => EsPortable ? DataDir : null;

        // Carpetas donde se buscan los modelos, en orden. La elegida en
        // Configuración va primero.
        public static List<string> CarpetasModelos(string? elegida)
        {
            var lista = new List<string>();
            if (!string.IsNullOrWhiteSpace(elegida)) lista.Add(elegida.Trim());
            if (EsPortable)
            {
                lista.Add(Path.Combine(RaizPortable!, "Models"));
                lista.Add(Path.Combine(RaizUnidad!, "MODELS"));
            }
            else
            {
                lista.Add(DevModelos);
            }
            return lista.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // El .gguf del LLM, buscado por nombre en cada carpeta y hasta tres
        // niveles adentro (E:\Models lo tiene en unsloth\Qwen3.5-9B-GGUF\).
        public static string? BuscarModeloLlm(string? elegida) =>
            Buscar(elegida, dir => Directory.EnumerateFiles(dir, ModeloLlm, Opciones).FirstOrDefault());

        // La carpeta del modelo de embeddings (con modules.json, formato de
        // sentence-transformers). null = la caché de HuggingFace de este
        // usuario, como antes.
        public static string? BuscarModeloEmbeddings(string? elegida) =>
            Buscar(elegida, dir =>
            {
                string directo = Path.Combine(dir, ModeloEmbeddings);
                if (EsModeloEmbeddings(directo)) return directo;
                return Directory.EnumerateDirectories(dir, ModeloEmbeddings, Opciones).FirstOrDefault(EsModeloEmbeddings);
            });

        private static bool EsModeloEmbeddings(string dir) =>
            File.Exists(Path.Combine(dir, "modules.json")) && File.Exists(Path.Combine(dir, "config.json"));

        private static readonly EnumerationOptions Opciones = new()
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        private static string? Buscar(string? elegida, Func<string, string?> buscarEn)
        {
            foreach (string dir in CarpetasModelos(elegida))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    string? hallado = buscarEn(dir);
                    if (hallado != null) return hallado;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        // Carpeta raíz de los PDF: la elegida en Configuración o, en modo
        // portable, <unidad>\BASE si existe. null = sin biblioteca (las rutas
        // se guardan absolutas, como antes).
        public static string? CarpetaBiblioteca(string? elegida)
        {
            if (!string.IsNullOrWhiteSpace(elegida)) return elegida.Trim();
            if (EsPortable)
            {
                string porDefecto = Path.Combine(RaizUnidad!, "BASE");
                if (Directory.Exists(porDefecto)) return porDefecto;
            }
            return null;
        }
    }
}
