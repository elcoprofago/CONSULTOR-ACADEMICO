using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using W = DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace ConsultorAcademicoGui
{
    public partial class MainWindow : Window
    {
        private const int BackendPort = 8001;
        internal static readonly string BackendBaseUrl = $"http://127.0.0.1:{BackendPort}";

        // Python, backend, llama-server y modelos: ver Entorno.cs (portable o
        // desarrollo).
        private const int LlamaPort = 9001;

        private const int PageSize = 10;

        // Backend Python (embeddings + FAISS de una sola biblioteca, mucho más
        // chica que CSJN+PGN) + llama-server (mismo modelo, mismo prompt
        // cache de 8 GiB que en CONSULTOR-GUI: ver "prompt cache is enabled,
        // size limit: 8192 MiB") -- el grueso del consumo es el LLM en sí, no
        // el índice FAISS. Valor de partida a recalibrar con carga real (ver
        // plan, "Open questions": la máquina tiene ~47 GB físicos y
        // CSJN+PGN solos ya llegan a ~20 GB, así que 16 GB es un punto medio
        // razonable para un backend con un corpus mucho menor mientras no
        // haya una medición real).
        private const double RamCapMb = 16000;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Sin timeout corto: una ingesta puede implicar descargar un PDF
        // grande por URL y convertir/embeber muchas páginas antes de que
        // /ingestar responda; una consulta relajada sobre una biblioteca ya
        // grande también puede tardar por el costo de normalizar() por chunk
        // (mismo patrón que CONSULTOR-GUI).
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private Process? _backendProcess;
        private Process? _llamaProcess;
        private int _backendPid = -1;

        private List<FragmentoDto> _currentFragmentos = new();
        private string? _currentRespuesta;
        private int _currentPage = 0;
        private readonly List<SessionEntry> _historial = new();
        private readonly bool _autoScrollLog = true;

        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTime _lastSampleTime = DateTime.MinValue;
        private ulong _lastGpuRaw = 0;
        private ulong _lastGpuTimeStamp = 0;
        private DispatcherTimer? _perfTimer;

        private GridLength? _colLogGuardado;
        private Window? _ventanaLog;
        private GridLength _colLogAntesDeDesacoplar = new(1, GridUnitType.Star);
        private bool _cerrandoApp;

        private AppSettings _settings = new();
        private bool _avisoPdfsSinUbicarMostrado;
        private bool _avisoCatalogoMostrado;
        private BitmapImage? _iconoConfigNormal;
        private BitmapImage? _iconoConfigHover;

        public MainWindow()
        {
            InitializeComponent();
            CircleRam.SetSize(48);
            CircleCpu.SetSize(48);
            CircleGpu.SetSize(48);

            _settings = CargarSettings();
            CargarIconosConfiguracion();
            AplicarTema();
            TxtK.Text = (_settings.KResultadosDefault ?? 5).ToString();
            ActualizarHistorialBusquedas();
            BtnActualizar.Visibility = Entorno.EsPortable ? Visibility.Visible : Visibility.Collapsed;

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_settings.LogDesacoplado) DesacoplarLog();
            Log($"Consultor Académico {VersionApp}" + (Entorno.EsPortable ? $" (portable: {Entorno.RaizPortable})" : " (desarrollo)"));
            InformarResultadoActualizacion();
            bool backendListo = await IniciarBackendAsync();
            // Después del backend y no antes (mismo criterio que CONSULTOR-GUI:
            // el backend necesita su margen de commit y llama va al final).
            if (backendListo) await IniciarLlamaSiHaceFaltaAsync();
            await CargarFiltroAsync();
            SetupPerfTimer();
        }

        // ===================== Configuración (botón de engranaje) =====================

        // Portable: data\config.json de esta copia. Desarrollo: Documentos.
        private static string GetConfigPath()
        {
            string dir = Entorno.DataDir;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        private static AppSettings CargarSettings()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path)) return new AppSettings();
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            catch
            {
                // Config corrupta o ilegible: seguir con los valores por
                // defecto en vez de impedir que la app arranque.
                return new AppSettings();
            }
        }

        private void GuardarSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetConfigPath(), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"No se pudo guardar la configuración: {ex.Message}", "WARN");
            }
        }

        private void CargarIconosConfiguracion()
        {
            _iconoConfigNormal = new BitmapImage(new Uri("pack://application:,,,/Assets/rueda1.png"));
            _iconoConfigHover = new BitmapImage(new Uri("pack://application:,,,/Assets/rueda2.png"));
            ImgConfiguracion.Source = _iconoConfigNormal;
            BtnConfiguracion.MouseEnter += (s, e) => ImgConfiguracion.Source = _iconoConfigHover;
            BtnConfiguracion.MouseLeave += (s, e) => ImgConfiguracion.Source = _iconoConfigNormal;
        }

        // Además del fondo de la ventana (Window.Background), el tema "Con
        // textura" pinta también el fondo de los paneles de búsqueda,
        // rendimiento (RAM/CPU/GPU) y botones inferiores con la misma
        // imagen/color. Resultados y Log quedan explícitamente afuera (mismo
        // criterio que CONSULTOR-GUI): una textura detrás de texto denso de
        // fragmentos perjudica la lectura más de lo que aporta.
        private void AplicarTema()
        {
            Brush fondoPaneles = (Brush)FindResource("PanelBg");

            switch (_settings.Tema)
            {
                case "Claro":
                    Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
                    break;
                case "Textura":
                    Brush? fondoMosaico = null;
                    if (!string.IsNullOrEmpty(_settings.RutaTextura) && File.Exists(_settings.RutaTextura))
                    {
                        try
                        {
                            var bmp = new BitmapImage(new Uri(_settings.RutaTextura));
                            // Mosaico a tamaño nativo (TileMode.Tile con Viewport
                            // absoluto = tamaño real de la imagen) en vez de un
                            // único Stretch ajustado al tamaño de cada panel: así
                            // la textura nunca se recorta ni se deforma sea cual
                            // sea la forma del panel, y se ve igual de uniforme
                            // en todos (ventana, búsqueda, rendimiento y botones).
                            fondoMosaico = new ImageBrush(bmp)
                            {
                                TileMode = TileMode.Tile,
                                Viewport = new Rect(0, 0, bmp.Width, bmp.Height),
                                ViewportUnits = BrushMappingMode.Absolute
                            };
                        }
                        catch (Exception ex)
                        {
                            Log($"No se pudo cargar la imagen de textura: {ex.Message}", "WARN");
                        }
                    }
                    fondoMosaico ??= new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                    Background = fondoMosaico;
                    fondoPaneles = fondoMosaico;
                    break;
                default: // "Oscuro"
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                    break;
            }

            BorderBusqueda.Background = fondoPaneles;
            BorderPerf.Background = fondoPaneles;
            BorderBotones.Background = fondoPaneles;
        }

        private void BtnConfiguracion_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new SettingsWindow(_settings) { Owner = this };
            if (wnd.ShowDialog() != true || wnd.Result == null) return;

            bool requiereReinicioBackend =
                wnd.Result.UsarGpuBusqueda != _settings.UsarGpuBusqueda ||
                wnd.Result.MmrLambdaDefault != _settings.MmrLambdaDefault ||
                wnd.Result.CarpetaBiblioteca != _settings.CarpetaBiblioteca ||
                wnd.Result.CarpetaModelos != _settings.CarpetaModelos;

            bool cambioCatalogo = wnd.Result.RutaCatalogoCitaPdf != _settings.RutaCatalogoCitaPdf;
            _settings = wnd.Result;
            GuardarSettings();
            if (cambioCatalogo) InformarCatalogo();
            AplicarTema();
            RenderPage();
            Log("Configuración guardada.", "OK");

            if (requiereReinicioBackend)
                Log("Los cambios de GPU, diversidad (MMR), biblioteca o modelos se aplican recién en el próximo inicio del backend: cerrá y volvé a abrir el consultor para que tomen efecto.", "WARN");
        }

        // ===================== Ciclo de vida del backend =====================

        // Evita que el usuario dispare /documentos o /consultar mientras el
        // backend todavía está cargando embeddings/FAISS (ventana en la que
        // la UI no está bloqueada por ser todo async, pero el puerto 8001
        // todavía no escucha), lo que antes producía un error prematuro que
        // nunca se reintentaba.
        private void SetControlesListos(bool listo)
        {
            BtnFiltroToggle.IsEnabled = listo;
            BtnAgregarMaterial.IsEnabled = listo;
            BtnConsultar.IsEnabled = listo;
        }

        // Arranca llama-server solo si nadie lo levantó ya en el puerto propio.
        private async Task IniciarLlamaSiHaceFaltaAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"http://127.0.0.1:{LlamaPort}/health");
                if (resp.IsSuccessStatusCode)
                {
                    Log($"llama-server ya estaba corriendo (puerto {LlamaPort}); no se lanza otro.", "INFO");
                    return;
                }
            }
            catch { /* no hay nada escuchando: hay que lanzarlo */ }
            BtnIniciarLlama_Click(this, new RoutedEventArgs());
        }

        // true cuando el backend responde /health; false si no arrancó o no respondió a tiempo.
        private async Task<bool> IniciarBackendAsync()
        {
            SetControlesListos(false);
            Log("Iniciando servicio backend (FastAPI)...", "INFO");
            try
            {
                if (!File.Exists(Entorno.Python))
                {
                    Log($"No se encontró Python en {Entorno.Python}.", "ERROR");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = Entorno.Python,
                    Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {BackendPort}",
                    WorkingDirectory = Entorno.BackendDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                };
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                // Con stdout redirigido a un pipe (no a una consola), Python usa
                // buffering por bloque en vez de por línea salvo que se le pida
                // lo contrario -- cualquier print() sin flush=True puede quedar
                // retenido en el buffer interno de Python en vez de llegar al
                // pipe (y por lo tanto al Log de la GUI) de inmediato (mismo
                // ajuste que CONSULTOR-GUI).
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                // Leídas por config.py/consultar.py de ACADEMICO-SCRIPTS al
                // importar el módulo: fijan el device del embedder
                // (SentenceTransformer) y el peso de diversidad MMR. Sólo
                // surten efecto en un arranque nuevo del backend, no en
                // caliente -- ver BtnConfiguracion_Click.
                psi.EnvironmentVariables["ACADEMICO_GPU"] = _settings.UsarGpuBusqueda ? "1" : "0";
                psi.EnvironmentVariables["ACADEMICO_MMR_LAMBDA"] =
                    _settings.MmrLambdaDefault.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Rutas para BACKEND/main.py y config.py (ver Entorno.cs). Sin
                // la variable, cada uno usa la ruta de siempre de esta PC.
                void Fijar(string nombre, string? valor)
                {
                    if (valor != null) psi.EnvironmentVariables[nombre] = valor;
                    else psi.EnvironmentVariables.Remove(nombre);
                }
                Fijar("ACADEMICO_SCRIPTS_DIR", Entorno.ScriptsDir);
                Fijar("ACADEMICO_DATA_DIR", Entorno.IndiceDir);
                Fijar("ACADEMICO_PDFTOTEXT", Entorno.Pdftotext);
                string? embeddings = Entorno.BuscarModeloEmbeddings(_settings.CarpetaModelos);
                Fijar("ACADEMICO_EMBED_MODEL", embeddings);
                string? biblioteca = Entorno.CarpetaBiblioteca(_settings.CarpetaBiblioteca);
                Fijar("ACADEMICO_BIBLIOTECA_DIR", biblioteca);
                if (Entorno.EsPortable)
                {
                    // El Python de runtime\ no debe tomar paquetes ni
                    // configuración de un Python instalado en el equipo.
                    psi.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
                    psi.EnvironmentVariables.Remove("PYTHONPATH");
                    psi.EnvironmentVariables.Remove("PYTHONHOME");
                }

                Log(Entorno.EsPortable ? $"Modo portable: {Entorno.RaizPortable}" : "Modo desarrollo (rutas de esta PC).", "INFO");
                Log(embeddings != null
                    ? $"Modelo de embeddings: {embeddings}"
                    : $"No se encontró la carpeta {Entorno.ModeloEmbeddings} en {string.Join(", ", Entorno.CarpetasModelos(_settings.CarpetaModelos))}; se usa la caché de HuggingFace de este usuario.",
                    embeddings != null ? "INFO" : "WARN");
                Log(biblioteca != null
                    ? $"Biblioteca de PDF: {biblioteca} (las rutas de adentro se guardan relativas)."
                    : "Sin carpeta de biblioteca: las rutas de los PDF se guardan completas.", "INFO");

                _backendProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _backendProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Log(ev.Data, "INFO"); };
                _backendProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Log(ev.Data, "INFO"); };
                _backendProcess.Start();
                _backendPid = _backendProcess.Id;
                _backendProcess.BeginOutputReadLine();
                _backendProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log($"No se pudo iniciar el backend: {ex.Message}", "ERROR");
                return false;
            }

            // Carga embeddings + índice FAISS de la biblioteca antes de responder /health.
            for (int intento = 0; intento < 120; intento++)
            {
                try
                {
                    var resp = await _http.GetAsync($"{BackendBaseUrl}/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        Log("Backend listo.", "OK");
                        SetControlesListos(true);
                        return true;
                    }
                }
                catch { /* aún no levantó */ }
                await Task.Delay(1000);
            }
            Log("El backend no respondió a tiempo. Revisá el log de arriba.", "ERROR");
            return false;
        }

        // ===================== Filtro de documentos =====================

        // Etiqueta que ve el usuario en el desplegable (Requisito 18):
        // degrada con gracia si falta autor y/o año, mismo criterio que la
        // cita formateada del backend (_cita() en responder_api.py).
        private static string FormatearLabelDocumento(DocumentoDto doc)
        {
            bool tieneAutor = !string.IsNullOrWhiteSpace(doc.autor);
            bool tieneAnio = !string.IsNullOrWhiteSpace(doc.anio);
            if (tieneAutor && tieneAnio) return $"{doc.titulo} — {doc.autor} ({doc.anio})";
            if (tieneAutor) return $"{doc.titulo} — {doc.autor}";
            if (tieneAnio) return $"{doc.titulo} ({doc.anio})";
            return doc.titulo;
        }

        private async Task CargarFiltroAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{BackendBaseUrl}/documentos");
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<List<DocumentoDto>>(body, JsonOpts) ?? new List<DocumentoDto>();
                var items = data
                    .Select(d => new CheckableItem
                    {
                        Value = d.documento_id,
                        Label = FormatearLabelDocumento(d),
                        Titulo = d.titulo,
                        Autor = d.autor,
                        Anio = d.anio,
                        FuenteEditorial = d.fuente_editorial,
                        RutaArchivo = d.ruta_archivo,
                        CopiaInterna = d.copia_interna,
                        Hash = d.hash_sha256,
                        FichaVinculada = d.ficha_citapdf,
                    })
                    .ToList();
                // Conserva las marcas de filtro al recargar (tras agregar
                // material o ubicar un PDF).
                if (ListFiltro.ItemsSource is List<CheckableItem> anteriores)
                {
                    var marcados = anteriores.Where(i => i.IsChecked).Select(i => i.Value).ToHashSet();
                    foreach (var i in items) i.IsChecked = marcados.Contains(i.Value);
                }
                ListFiltro.ItemsSource = items;
                ActualizarResumenFiltro();

                if (!_avisoPdfsSinUbicarMostrado)
                {
                    _avisoPdfsSinUbicarMostrado = true;
                    var sinUbicar = items.Where(Ubicador.FaltaArchivo).ToList();
                    if (sinUbicar.Count > 0)
                        Log($"{sinUbicar.Count} documento(s) sin PDF original en la ruta registrada " +
                            $"({string.Join(", ", sinUbicar.Select(i => i.Value))}). La búsqueda funciona igual; " +
                            "para abrirlos, usá el botón \"PDF\" de la lista de documentos, que permite ubicarlos.", "WARN");
                }
                if (!_avisoCatalogoMostrado)
                {
                    _avisoCatalogoMostrado = true;
                    InformarCatalogo();
                }
            }
            catch (Exception ex)
            {
                Log($"No se pudo cargar la lista de documentos: {ex.Message}", "ERROR");
                ListFiltro.ItemsSource = new List<CheckableItem>();
            }
        }

        private List<CheckableItem> DocumentosCargados() =>
            ListFiltro.ItemsSource as List<CheckableItem> ?? new List<CheckableItem>();

        // Estado del catálogo de CitaPDF en el log: al cargar la lista de
        // documentos por primera vez y al cambiar su ruta en Configuración.
        private void InformarCatalogo()
        {
            var catalogo = CatalogoCitaPdf.Cargar(_settings.RutaCatalogoCitaPdf);
            if (!catalogo.Disponible)
            {
                // Sin ruta elegida y sin CitaPDF en esta PC no es un problema:
                // la ficha es opcional.
                bool porDefectoAusente = string.IsNullOrWhiteSpace(_settings.RutaCatalogoCitaPdf) && !File.Exists(catalogo.Ruta);
                Log(porDefectoAusente
                        ? $"Fichas de CitaPDF: no se encontró su catálogo en {catalogo.Ruta}. Si usás CitaPDF portable, elegí su biblioteca.json en Configuración → Biblioteca."
                        : $"Fichas de CitaPDF no disponibles: {catalogo.Error}",
                    porDefectoAusente ? "INFO" : "WARN");
                return;
            }
            var docs = DocumentosCargados();
            int conFicha = docs.Count(d => CatalogoCitaPdf.FichaDe(d, catalogo).Ficha != null);
            var rotos = docs.Where(d => CatalogoCitaPdf.FichaDe(d, catalogo).Tipo == CatalogoCitaPdf.Vinculo.ManualNoEncontrado)
                .Select(d => d.Value).ToList();
            Log($"Catálogo de CitaPDF: {catalogo.Fichas.Count} registros ({catalogo.Ruta}). " +
                $"{conFicha} de {docs.Count} documento(s) con ficha; los demás se vinculan con el botón \"Ficha\".", "INFO");
            if (rotos.Count > 0)
                Log($"Vínculo con una ficha que ya no está en el catálogo: {string.Join(", ", rotos)}.", "WARN");
        }

        private List<string> FiltroSeleccionado()
        {
            var items = ListFiltro.ItemsSource as List<CheckableItem>;
            return items?.Where(i => i.IsChecked).Select(i => i.Value).ToList() ?? new List<string>();
        }

        private void FiltroCheckBox_Changed(object sender, RoutedEventArgs e) => ActualizarResumenFiltro();

        // Heurística simple (último token = apellido, resto = nombres -> iniciales):
        // "Autor" es texto libre pensado para un único autor en orden natural (como
        // se carga en "Agregar material", ej. "Pablo Eugenio Navarro"). No resuelve
        // apellidos compuestos ("De la Rúa") ni varios autores -- para esos casos,
        // corregir a mano el texto ya copiado al portapapeles.
        private static string FormatearAutorApa(string autor)
        {
            var partes = autor.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0) return "";
            if (partes.Length == 1) return partes[0];
            string apellido = partes[^1];
            string iniciales = string.Join(" ", partes[..^1].Select(p => $"{char.ToUpperInvariant(p[0])}."));
            return $"{apellido}, {iniciales}";
        }

        private static string ConstruirCitaApa(string autor, string anio, string titulo, string fuente)
        {
            string autorApa = FormatearAutorApa(autor);
            string anioParte = string.IsNullOrWhiteSpace(anio) ? "s.f." : anio;
            string cita = string.IsNullOrWhiteSpace(autorApa)
                ? $"{titulo} ({anioParte})."
                : $"{autorApa} ({anioParte}). {titulo}.";
            if (!string.IsNullOrWhiteSpace(fuente))
                cita += $" {fuente}.";
            return cita;
        }

        // Botón por fila de la lista de documentos (no del checkbox de filtro,
        // que tiene otro propósito -- restringir la búsqueda): copia la cita
        // APA de ESE documento con los datos editoriales ya guardados en
        // documentos.json, sin marcarlo/desmarcarlo como filtro de consulta.
        private void FiltroCopiarCita_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CheckableItem item) return;
            var (ficha, _) = CatalogoCitaPdf.FichaDe(item, CatalogoCitaPdf.Cargar(_settings.RutaCatalogoCitaPdf));
            if (ficha != null && !string.IsNullOrWhiteSpace(ficha.CitaApa))
            {
                Clipboard.SetText(ficha.CitaApa);
                Log($"Cita copiada al portapapeles (ficha CitaPDF {ficha.DocumentoId}): {ficha.CitaApa}", "OK");
                return;
            }
            string cita = ConstruirCitaApa(item.Autor, item.Anio, item.Titulo, item.FuenteEditorial);
            Clipboard.SetText(cita);
            Log($"Cita copiada al portapapeles: {cita}", "OK");
        }

        // Botón "Ficha" por fila: la ficha de CitaPDF del documento, y
        // vincularla a mano si su PDF no es el mismo archivo catalogado.
        private void FiltroFicha_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CheckableItem item) return;
            BtnFiltroToggle.IsChecked = false;
            var wnd = new FichaWindow(item, DocumentosCargados(), _settings.RutaCatalogoCitaPdf, (m, nivel) => Log(m, nivel))
                { Owner = this };
            wnd.ShowDialog();
            if (wnd.HuboCambios) RenderPage();
        }

        // Botón "PDF" por fila: abre el original desde su ruta registrada (el
        // consultor no guarda copia); si no está, ofrece ubicarlo.
        private async void FiltroAbrirPdf_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CheckableItem item) return;
            if (ListFiltro.ItemsSource is not List<CheckableItem> todos) return;
            BtnFiltroToggle.IsChecked = false;
            if (UbicarPdfWindow.AbrirDocumento(this, item, todos))
            {
                Log($"Ruta del PDF registrada: {item.Value} -> {item.RutaArchivo}", "OK");
                await CargarFiltroAsync();
            }
        }

        private void ActualizarResumenFiltro()
        {
            var items = ListFiltro.ItemsSource as List<CheckableItem>;
            int n = items?.Count(i => i.IsChecked) ?? 0;
            BtnFiltroToggle.Content = n == 0 ? "(todos)" : $"{n} seleccionados";
        }

        // ===================== Consulta =====================

        private void TxtPregunta_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConsultar_Click(sender, e);
        }

        // ===================== Historial de búsquedas =====================

        private void ActualizarHistorialBusquedas()
        {
            ListHistorial.ItemsSource = _settings.HistorialBusquedas.ToList();
            BtnHistorialToggle.IsEnabled = _settings.HistorialBusquedas.Count > 0;
        }

        private void RegistrarBusquedaEnHistorial(string pregunta)
        {
            _settings.HistorialBusquedas.RemoveAll(p => string.Equals(p, pregunta, StringComparison.OrdinalIgnoreCase));
            _settings.HistorialBusquedas.Insert(0, pregunta);
            if (_settings.HistorialBusquedas.Count > 10)
                _settings.HistorialBusquedas.RemoveRange(10, _settings.HistorialBusquedas.Count - 10);
            GuardarSettings();
            ActualizarHistorialBusquedas();
        }

        private void HistorialItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is string pregunta)
            {
                TxtPregunta.Text = pregunta;
                TxtPregunta.CaretIndex = pregunta.Length;
                BtnHistorialToggle.IsChecked = false;
                TxtPregunta.Focus();
            }
        }

        private void TxtK_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private async void BtnConsultar_Click(object sender, RoutedEventArgs e)
        {
            string pregunta = TxtPregunta.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(pregunta))
            {
                Log("Escribí una pregunta o frase antes de consultar.", "WARN");
                return;
            }

            var filtro = FiltroSeleccionado();
            int k = int.TryParse(TxtK.Text, out int kk) && kk > 0 ? kk : 5;

            RegistrarBusquedaEnHistorial(pregunta);

            BtnConsultar.IsEnabled = false;
            string filtroTxt = filtro.Count > 0 ? $" [{string.Join(", ", filtro)}]" : "";
            Log($"Consultando{filtroTxt}: \"{pregunta}\"", "INFO");

            try
            {
                var req = new ConsultaRequest
                {
                    pregunta = pregunta,
                    filtro = filtro.Count > 0 ? filtro : null,
                    k = k,
                };
                var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync($"{BackendBaseUrl}/consultar", content);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Log($"El backend respondió {(int)resp.StatusCode}: {body}", "ERROR");
                    return;
                }

                var data = JsonSerializer.Deserialize<ConsultaResponseDto>(body, JsonOpts);
                if (data == null)
                {
                    Log("Respuesta vacía del backend.", "ERROR");
                    return;
                }

                Log($"Rama utilizada: {data.rama}", "INFO");

                // Diagnóstico operativo: de los fragmentos hallados/mostrados (que
                // pueden ser muchos), cuáles fueron los pocos que realmente se le
                // mandaron al LLM como contexto (tope K_MAX_CONTEXTO_LLM en el
                // backend). Esto NO es la respuesta -- son las citas usadas, útil
                // para entender por qué la respuesta no menciona algo que sí
                // aparece en un fragmento mostrado pero no seleccionado.
                if (data.citas_contexto != null && data.citas_contexto.Count > 0)
                    Log($"Fragmentos enviados al LLM como contexto ({data.citas_contexto.Count}): {string.Join(" | ", data.citas_contexto)}", "INFO");

                if (!string.IsNullOrEmpty(data.error_llm))
                    Log($"El LLM (llama-server) no respondió: {data.error_llm}", "ERROR");

                if (!string.IsNullOrEmpty(data.mensaje))
                    Log(data.mensaje, "INFO");

                _currentRespuesta = data.respuesta;
                _currentFragmentos = data.fragmentos ?? new List<FragmentoDto>();
                _currentPage = 0;
                RenderPage();

                Log(_currentFragmentos.Count > 0
                    ? $"Consulta finalizada: {_currentFragmentos.Count} fragmento(s) encontrado(s)."
                    : "Consulta finalizada: no surge de los fragmentos proporcionados.", "OK");

                _historial.Add(new SessionEntry
                {
                    Timestamp = DateTime.Now,
                    Pregunta = pregunta,
                    Filtro = filtro,
                    K = k,
                    Rama = data.rama,
                    Respuesta = data.respuesta,
                    Mensaje = data.mensaje,
                    ErrorLlm = data.error_llm,
                    Fragmentos = _currentFragmentos,
                });
            }
            catch (TaskCanceledException)
            {
                Log("La consulta superó el tiempo máximo de espera (timeout).", "ERROR");
            }
            catch (HttpRequestException ex)
            {
                Log($"No se pudo contactar al servicio backend: {ex.Message}", "ERROR");
            }
            catch (Exception ex)
            {
                Log($"Error inesperado al consultar: {ex.Message}", "ERROR");
            }
            finally
            {
                BtnConsultar.IsEnabled = true;
            }
        }

        // ===================== Agregar material =====================

        private void BtnAgregarMaterial_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new AgregarMaterialWindow(Log, CargarFiltroAsync, _settings.CarpetaDescargas,
                                                _settings.RutaCatalogoCitaPdf, DocumentosCargados) { Owner = this };
            wnd.ShowDialog();
        }

        // ===================== Resultados / paginación =====================

        private void RenderPage()
        {
            PanelResultados.Children.Clear();

            // La respuesta sintetizada por el LLM antes sólo quedaba en el
            // panel de log (una línea más entre el resto del texto de
            // diagnóstico); se muestra acá primero, destacada, porque es el
            // resultado que el usuario efectivamente vino a buscar.
            if (!string.IsNullOrEmpty(_currentRespuesta))
            {
                var respuestaBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFB)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x47, 0x6B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 0, 12),
                    Padding = new Thickness(10),
                };
                var respuestaStack = new StackPanel();
                respuestaStack.Children.Add(new TextBlock
                {
                    Text = "Respuesta",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0D, 0x47, 0x6B)),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                respuestaStack.Children.Add(MakeSelectableText(
                    _currentRespuesta!,
                    new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                    FontWeights.Normal,
                    _settings.TamanoFuenteResultados));
                respuestaBorder.Child = respuestaStack;
                PanelResultados.Children.Add(respuestaBorder);
            }

            if (_currentFragmentos.Count == 0)
            {
                TxtContador.Text = "";
                BtnAnterior.IsEnabled = false;
                BtnSiguiente.IsEnabled = false;
                return;
            }

            int totalPages = (int)Math.Ceiling(_currentFragmentos.Count / (double)PageSize);
            _currentPage = Math.Clamp(_currentPage, 0, totalPages - 1);

            int inicio = _currentPage * PageSize;
            var pagina = _currentFragmentos.Skip(inicio).Take(PageSize).ToList();

            var catalogo = CatalogoCitaPdf.Cargar(_settings.RutaCatalogoCitaPdf);
            var docsPorId = DocumentosCargados().ToDictionary(d => d.Value);

            foreach (var frag in pagina)
            {
                var border = new Border
                {
                    // Fondo claro tipo "papel" para que el texto denso de las
                    // citas se lea con comodidad.
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xC0)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10),
                };
                var stack = new StackPanel();
                var citaBox = MakeSelectableText(
                    frag.cita,
                    new SolidColorBrush(Color.FromRgb(0x0D, 0x47, 0x6B)),
                    FontWeights.SemiBold,
                    _settings.TamanoFuenteResultados);
                citaBox.Margin = new Thickness(0, 0, 0, 4);
                stack.Children.Add(citaBox);
                if (docsPorId.TryGetValue(frag.documento_id, out var doc) &&
                    CatalogoCitaPdf.FichaDe(doc, catalogo).Ficha is { } ficha)
                    stack.Children.Add(CrearFichaAdjunta(ficha));
                stack.Children.Add(MakeSelectableText(
                    frag.texto,
                    new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                    FontWeights.Normal,
                    _settings.TamanoFuenteResultados));
                border.Child = stack;
                PanelResultados.Children.Add(border);
            }

            int desde = inicio + 1;
            int hasta = inicio + pagina.Count;
            TxtContador.Text = $"{desde}-{hasta} de {_currentFragmentos.Count}";
            BtnAnterior.IsEnabled = _currentPage > 0;
            BtnSiguiente.IsEnabled = _currentPage < totalPages - 1;
        }

        // Ficha de CitaPDF bajo la cita del fragmento, plegada para no
        // alargar cada resultado.
        private Expander CrearFichaAdjunta(FichaCitaPdf ficha)
        {
            var oscuro = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var tenue = new SolidColorBrush(Color.FromRgb(0x66, 0x5E, 0x50));
            double tam = Math.Max(10, _settings.TamanoFuenteResultados - 1);
            var cuerpo = new StackPanel { Margin = new Thickness(18, 4, 0, 4) };
            if (!string.IsNullOrWhiteSpace(ficha.CitaApa))
                cuerpo.Children.Add(MakeSelectableText(ficha.CitaApa, oscuro, FontWeights.Normal, tam));
            var datos = new List<string>();
            if (!string.IsNullOrWhiteSpace(ficha.Editorial)) datos.Add(ficha.Editorial);
            if (!string.IsNullOrWhiteSpace(ficha.RutaArchivoOriginal)) datos.Add($"PDF catalogado: {ficha.RutaArchivoOriginal}");
            if (!string.IsNullOrWhiteSpace(ficha.OrigenUrl)) datos.Add($"URL: {ficha.OrigenUrl}");
            if (datos.Count > 0)
                cuerpo.Children.Add(MakeSelectableText(string.Join("\n", datos), tenue, FontWeights.Normal, tam - 1));
            var copiar = new Button
            {
                Content = "Copiar cita",
                FontSize = 10,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = !string.IsNullOrWhiteSpace(ficha.CitaApa),
            };
            copiar.Click += (_, _) =>
            {
                Clipboard.SetText(ficha.CitaApa);
                Log($"Cita copiada al portapapeles (ficha CitaPDF {ficha.DocumentoId}): {ficha.CitaApa}", "OK");
            };
            cuerpo.Children.Add(copiar);
            return new Expander
            {
                Header = new TextBlock { Text = $"Ficha CitaPDF · {ficha.DocumentoId}", Foreground = tenue, FontSize = tam - 1 },
                Content = cuerpo,
                Foreground = oscuro,
                Margin = new Thickness(0, 0, 0, 6),
            };
        }

        private void BtnAnterior_Click(object sender, RoutedEventArgs e) { _currentPage--; RenderPage(); }
        private void BtnSiguiente_Click(object sender, RoutedEventArgs e) { _currentPage++; RenderPage(); }

        // TextBlock no permite seleccionar/copiar texto en WPF; se usa un
        // TextBox de solo lectura sin borde ni fondo propio para que se vea
        // igual que antes pero el usuario pueda copiar manualmente la
        // respuesta y las citas/fragmentos.
        private static TextBox MakeSelectableText(string text, Brush foreground, FontWeight weight, double fontSize = 13.0)
        {
            return new TextBox
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = foreground,
                FontWeight = weight,
                FontSize = fontSize,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                AcceptsReturn = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }

        private void BtnLimpiarResultados_Click(object sender, RoutedEventArgs e)
        {
            _currentFragmentos = new List<FragmentoDto>();
            _currentRespuesta = null;
            _currentPage = 0;
            RenderPage();
            Log("Panel de resultados vacío.", "INFO");
        }

        // ===================== llama-server (proceso hijo) =====================

        private void BtnIniciarLlama_Click(object sender, RoutedEventArgs e)
        {
            if (_llamaProcess != null && !_llamaProcess.HasExited)
            {
                Log("llama-server ya está corriendo.", "WARN");
                return;
            }

            string llamaServer = Entorno.LlamaServer;
            if (!File.Exists(llamaServer))
            {
                Log($"No se encontró {llamaServer}.", "ERROR");
                return;
            }
            string? modelo = Entorno.BuscarModeloLlm(_settings.CarpetaModelos);
            if (modelo == null)
            {
                Log($"No se encontró {Entorno.ModeloLlm} en {string.Join(", ", Entorno.CarpetasModelos(_settings.CarpetaModelos))}. " +
                    "Copialo a una de esas carpetas o elegí la carpeta de modelos en Configuración.", "ERROR");
                return;
            }
            Log($"Modelo: {modelo}", "INFO");

            Log($"Iniciando llama-server (puerto {LlamaPort})...", "INFO");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = llamaServer,
                    WorkingDirectory = Path.GetDirectoryName(llamaServer),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelo);
                psi.ArgumentList.Add("--host"); psi.ArgumentList.Add("127.0.0.1");
                psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(LlamaPort.ToString());
                psi.ArgumentList.Add("--ctx-size"); psi.ArgumentList.Add("8192");
                psi.ArgumentList.Add("--n-gpu-layers"); psi.ArgumentList.Add("99");
                psi.ArgumentList.Add("--parallel"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("--flash-attn"); psi.ArgumentList.Add("on");
                psi.ArgumentList.Add("--cache-type-k"); psi.ArgumentList.Add("q8_0");
                psi.ArgumentList.Add("--cache-type-v"); psi.ArgumentList.Add("q8_0");

                // Qwen3.5 razona por defecto y ese razonamiento gastaría los
                // max_tokens de consultar.py antes de responder.
                psi.ArgumentList.Add("--reasoning"); psi.ArgumentList.Add("off");

                _llamaProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _llamaProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Log($"[llama-server] {ev.Data}", "INFO"); };
                _llamaProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Log($"[llama-server] {ev.Data}", "INFO"); };
                _llamaProcess.Exited += (s, ev) => Dispatcher.Invoke(() =>
                {
                    Log("llama-server se detuvo.", "WARN");
                    BtnIniciarLlama.Content = "Iniciar llama-server";
                });
                _llamaProcess.Start();
                _llamaProcess.BeginOutputReadLine();
                _llamaProcess.BeginErrorReadLine();

                BtnIniciarLlama.Content = "llama-server corriendo";
                Log("llama-server lanzado. Puede tardar unos segundos en cargar el modelo.", "OK");
            }
            catch (Exception ex)
            {
                Log($"No se pudo iniciar llama-server: {ex.Message}", "ERROR");
            }
        }

        // ===================== Purgar procesos huérfanos =====================

        // Reiniciar el backend muchas veces seguidas durante una sesión de
        // pruebas puede dejar instancias huérfanas corriendo si
        // CONSULTOR-ACADEMICO-GUI.exe se cierra sin pasar por Window_Closing
        // (crash, "Detener" desde el IDE, etc.) -- Window_Closing sí mata su
        // propio árbol con Kill(true), pero no puede hacer nada por
        // instancias de corridas anteriores. Mismo patrón que
        // CONSULTOR-GUI, con un agregado importante: como acá VenvPython y
        // LlamaServerExe son literalmente los mismos ejecutables que usa
        // CONSULTOR-GUI (venv compartido, mismo llama-server.exe), el
        // fingerprint no puede basarse sólo en exe+"main:app" -- si el
        // usuario tiene ambas GUIs abiertas a la vez, ese criterio marcaría
        // el backend/llama-server VIVO de la otra app como "huérfano" de
        // ésta y lo mataría. Se agrega el puerto propio (--port 8001 / 9001)
        // a la huella para distinguirlos.
        private void BtnPurgar_Click(object sender, RoutedEventArgs e) => PurgarProcesosHuerfanos(pedirConfirmacion: true);

        // pedirConfirmacion=false se usa desde Window_Closing (auto-purgar al
        // cerrar, ver ChkAutoPurgar en SettingsWindow): a esa altura ya no hay
        // vuelta atrás (la ventana se está cerrando), así que un MessageBox
        // modal sólo bloquearía el cierre sin aportar nada.
        private void PurgarProcesosHuerfanos(bool pedirConfirmacion)
        {
            // Raíces propias (las que esta misma ventana lanzó y siguen
            // vivas): no hay que tocarlas aunque coincidan por ruta/línea de
            // comando con lo que se busca.
            var propios = new HashSet<int>();
            if (_backendProcess != null && !_backendProcess.HasExited) propios.Add(_backendProcess.Id);
            if (_llamaProcess != null && !_llamaProcess.HasExited) propios.Add(_llamaProcess.Id);

            string puertoBackendTag = $"--port {BackendPort}";
            string puertoLlamaTag = $"--port {LlamaPort}";

            var huerfanos = new List<(int Pid, string Nombre)>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process " +
                    "WHERE Name = 'python.exe' OR Name = 'llama-server.exe'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    if (propios.Contains(pid)) continue;

                    string exe = mo["ExecutablePath"]?.ToString() ?? "";
                    string cmd = mo["CommandLine"]?.ToString() ?? "";
                    string name = mo["Name"]?.ToString() ?? "";

                    // El backend propio se identifica por su venv exacto + el
                    // módulo que arranca + su puerto propio (8001): no
                    // alcanza con "python.exe" (cualquier otro script podría
                    // estar corriendo) ni con exe+"main:app" a secas (eso
                    // también matchea el backend de CONSULTOR-GUI, que
                    // comparte el mismo venv y el mismo nombre de módulo pero
                    // corre en el puerto 8000).
                    bool esBackendNuestro = exe.Equals(Entorno.Python, StringComparison.OrdinalIgnoreCase)
                        && cmd.Contains("uvicorn", StringComparison.OrdinalIgnoreCase)
                        && cmd.Contains("main:app", StringComparison.OrdinalIgnoreCase)
                        && cmd.Contains(puertoBackendTag, StringComparison.OrdinalIgnoreCase);
                    // Mismo razonamiento para llama-server: exe idéntico al de
                    // CONSULTOR-GUI (mismo binario), distinguido por puerto
                    // (9001 acá vs. 9000 en CONSULTOR-GUI).
                    bool esLlamaNuestro = exe.Equals(Entorno.LlamaServer, StringComparison.OrdinalIgnoreCase)
                        && cmd.Contains(puertoLlamaTag, StringComparison.OrdinalIgnoreCase);

                    if (esBackendNuestro || esLlamaNuestro)
                        huerfanos.Add((pid, name));
                }
            }
            catch (ManagementException ex)
            {
                Log($"No se pudo consultar los procesos del sistema: {ex.Message}", "ERROR");
                return;
            }

            if (huerfanos.Count == 0)
            {
                Log("No se encontraron procesos huérfanos de CONSULTOR-ACADEMICO-GUI.", "OK");
                return;
            }

            if (pedirConfirmacion)
            {
                string listado = string.Join("\n", huerfanos.Select(h => $"  PID {h.Pid} ({h.Nombre})"));
                var confirmar = MessageBox.Show(
                    this,
                    $"Se encontraron {huerfanos.Count} proceso(s) huérfano(s) de CONSULTOR-ACADEMICO-GUI:\n\n{listado}\n\n¿Terminarlos?",
                    "Purgar procesos huérfanos",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmar != MessageBoxResult.Yes) return;
            }

            foreach (var (pid, name) in huerfanos)
            {
                try
                {
                    // /T mata todo el árbol descendiente de ese PID (p. ej. el
                    // intérprete base que el venv python.exe relanza como
                    // hijo, ver comentario en PerfTimer_Tick).
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {pid} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(5000);
                    Log($"Proceso huérfano eliminado: {name} (PID {pid}).", "OK");
                }
                catch (Exception ex)
                {
                    Log($"No se pudo eliminar el proceso {name} (PID {pid}): {ex.Message}", "WARN");
                }
            }
        }

        // ===================== Exportar resultados (docx/pdf/txt) =====================

        private void BtnExportar_Click(object sender, RoutedEventArgs e)
        {
            var fragmentos = RbVistaTodos.IsChecked == true
                ? _currentFragmentos
                : _currentFragmentos.Skip(_currentPage * PageSize).Take(PageSize).ToList();

            if (fragmentos.Count == 0)
            {
                Log("No hay fragmentos para exportar.", "WARN");
                return;
            }

            string formato = _settings.FormatoExportacion;
            string filtro = formato switch
            {
                "pdf" => "Documento PDF (*.pdf)|*.pdf",
                "txt" => "Texto plano (*.txt)|*.txt",
                _ => "Documento Word (*.docx)|*.docx",
            };

            var dlg = new SaveFileDialog
            {
                Filter = filtro,
                FileName = $"consulta_{DateTime.Now:yyyyMMdd_HHmmss}.{formato}",
            };
            if (!string.IsNullOrEmpty(_settings.CarpetaExportacionDefault) && Directory.Exists(_settings.CarpetaExportacionDefault))
                dlg.InitialDirectory = _settings.CarpetaExportacionDefault;
            if (dlg.ShowDialog() != true) return;

            try
            {
                string pregunta = TxtPregunta.Text?.Trim() ?? "";
                switch (formato)
                {
                    case "pdf":
                        ExportarPdf(dlg.FileName, pregunta, _currentRespuesta, fragmentos);
                        break;
                    case "txt":
                        ExportarTxt(dlg.FileName, pregunta, _currentRespuesta, fragmentos);
                        break;
                    default:
                        ExportarDocx(dlg.FileName, pregunta, _currentRespuesta, fragmentos);
                        break;
                }
                Log($"Exportado: {dlg.FileName} ({fragmentos.Count} fragmento(s)).", "OK");
            }
            catch (Exception ex)
            {
                Log($"No se pudo exportar el documento: {ex.Message}", "ERROR");
            }
        }

        private static void ExportarTxt(string path, string pregunta, string? respuesta, List<FragmentoDto> fragmentos)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Pregunta: {pregunta}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(respuesta))
            {
                sb.AppendLine("Respuesta");
                sb.AppendLine(respuesta);
                sb.AppendLine();
            }

            foreach (var frag in fragmentos)
            {
                sb.AppendLine(frag.cita);
                sb.AppendLine(frag.texto);
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void ExportarPdf(string path, string pregunta, string? respuesta, List<FragmentoDto> fragmentos)
        {
            QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(30);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontSize(11));

                    page.Content().Column(col =>
                    {
                        col.Spacing(8);
                        col.Item().Text($"Pregunta: {pregunta}").Bold();

                        if (!string.IsNullOrEmpty(respuesta))
                        {
                            col.Item().PaddingTop(6).Text("Respuesta").Bold();
                            col.Item().Text(respuesta);
                        }

                        foreach (var frag in fragmentos)
                        {
                            col.Item().PaddingTop(6).Text(frag.cita).SemiBold();
                            col.Item().Text(frag.texto);
                        }
                    });
                });
            }).GeneratePdf(path);
        }

        private static void ExportarDocx(string path, string pregunta, string? respuesta, List<FragmentoDto> fragmentos)
        {
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);

            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new W.Document();
            var body = mainPart.Document.AppendChild(new W.Body());

            body.AppendChild(new W.Paragraph(new W.Run(
                new W.RunProperties(new W.Bold()), new W.Text($"Pregunta: {pregunta}"))));
            body.AppendChild(new W.Paragraph());

            if (!string.IsNullOrEmpty(respuesta))
            {
                body.AppendChild(new W.Paragraph(new W.Run(
                    new W.RunProperties(new W.Bold()), new W.Text("Respuesta"))));
                body.AppendChild(new W.Paragraph(new W.Run(
                    new W.Text(respuesta) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
                body.AppendChild(new W.Paragraph());
            }

            foreach (var frag in fragmentos)
            {
                body.AppendChild(new W.Paragraph(new W.Run(
                    new W.RunProperties(new W.Bold()), new W.Text(frag.cita))));
                body.AppendChild(new W.Paragraph(new W.Run(
                    new W.Text(frag.texto) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })));
                body.AppendChild(new W.Paragraph());
            }

            mainPart.Document.Save();
        }

        // ===================== Guardar / Cargar sesión (JSON) =====================

        // Carpeta fija y predecible (Documentos, o data\ en modo portable):
        // Guardar y Cargar siempre abren en el mismo lugar (mismo criterio que
        // CONSULTOR-GUI).
        private static string GetSesionesDir()
        {
            string dir = Path.Combine(Entorno.DataDir, "Sesiones");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void BtnGuardarSesion_Click(object sender, RoutedEventArgs e)
        {
            if (_historial.Count == 0)
            {
                Log("No hay historial de consultas para guardar en esta sesión.", "INFO");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Sesión CONSULTOR-ACADEMICO-GUI (*.json)|*.json",
                FileName = $"sesion_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                InitialDirectory = GetSesionesDir(),
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var session = new SessionFile { Historial = _historial };
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
                Log($"Sesión guardada: {dlg.FileName} ({_historial.Count} consulta(s)).", "OK");
            }
            catch (Exception ex)
            {
                Log($"No se pudo guardar la sesión: {ex.Message}", "ERROR");
            }
        }

        private void BtnCargarSesion_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Sesión CONSULTOR-ACADEMICO-GUI (*.json)|*.json",
                InitialDirectory = GetSesionesDir(),
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var json = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                var session = JsonSerializer.Deserialize<SessionFile>(json, JsonOpts);
                if (session == null || session.Historial.Count == 0)
                {
                    Log("El archivo de sesión no contiene historial.", "ERROR");
                    return;
                }

                _historial.Clear();
                _historial.AddRange(session.Historial);

                var ultima = session.Historial[^1];
                _currentFragmentos = ultima.Fragmentos;
                _currentRespuesta = ultima.Respuesta;
                _currentPage = 0;
                RenderPage();

                TxtPregunta.Text = ultima.Pregunta;

                Log($"Sesión cargada: {session.Historial.Count} consulta(s). Mostrando resultados de la última: \"{ultima.Pregunta}\".", "OK");
            }
            catch (Exception ex)
            {
                Log($"No se pudo cargar el archivo de sesión: {ex.Message}", "ERROR");
            }
        }

        // ===================== Círculos de rendimiento =====================

        private void SetupPerfTimer()
        {
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _perfTimer.Tick += PerfTimer_Tick;
            _perfTimer.Start();
        }

        private void PerfTimer_Tick(object? sender, EventArgs e)
        {
            if (_backendPid < 0) return;

            List<int> pids;
            try
            {
                // El venv python.exe en esta máquina reejecuta el intérprete base como
                // proceso hijo (el trabajo real -- carga de FAISS/embeddings -- ocurre
                // en ese hijo, no en el PID que devuelve Process.Start()). Por eso se
                // suma todo el árbol de procesos descendiente en vez de un solo PID.
                pids = GetProcessTreePids(_backendPid);

                // llama-server es un proceso hijo separado (no desciende del backend
                // Python) y es el único que efectivamente usa la GPU. Si no se suma
                // acá, el círculo de GPU queda en 0% aun cuando el LLM esté generando.
                if (_llamaProcess != null && !_llamaProcess.HasExited)
                {
                    foreach (int pid in GetProcessTreePids(_llamaProcess.Id))
                        if (!pids.Contains(pid)) pids.Add(pid);
                }
                else
                {
                    // _llamaProcess sólo se setea cuando el propio botón "Iniciar
                    // llama-server" de la GUI lo lanza. Si el usuario lo arranca
                    // por su cuenta, este proceso C# nunca se entera de su PID y
                    // el círculo de GPU quedaría en 0% aunque el LLM esté
                    // generando activamente: se lo busca acá también por nombre
                    // de imagen como respaldo.
                    foreach (var proc in Process.GetProcessesByName("llama-server"))
                    {
                        foreach (int pid in GetProcessTreePids(proc.Id))
                            if (!pids.Contains(pid)) pids.Add(pid);
                    }
                }
            }
            catch (ManagementException)
            {
                // WMI puede fallar transitoriamente (proveedor saturado por
                // consultas frecuentes); se salta este tick y se reintenta en
                // el próximo en vez de tirar abajo toda la aplicación.
                return;
            }

            double totalMb = 0;
            TimeSpan totalCpu = TimeSpan.Zero;
            bool anyAlive = false;
            foreach (int pid in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    p.Refresh();
                    totalMb += p.WorkingSet64 / 1024.0 / 1024.0;
                    totalCpu += p.TotalProcessorTime;
                    anyAlive = true;
                }
                catch { }
            }

            if (!anyAlive)
            {
                CircleRam.SetCircle(0, "no encontrado");
                CircleCpu.SetCircle(0, "no encontrado");
                CircleGpu.SetCircle(0, "no encontrado");
                return;
            }

            double ramPct = Math.Min(totalMb / RamCapMb * 100.0, 100.0);
            CircleRam.SetCircle(ramPct, $"RAM {totalMb:F0} MB");

            DateTime now = DateTime.UtcNow;
            if (_lastSampleTime != DateTime.MinValue)
            {
                double elapsedMs = (now - _lastSampleTime).TotalMilliseconds;
                double cpuMs = (totalCpu - _lastCpuTime).TotalMilliseconds;
                // 100% = mitad de los núcleos lógicos de la máquina saturados
                // (mismo criterio calibrado en CONSULTOR-GUI: el árbol del
                // backend Python más llama-server arrancan varios hilos cada
                // uno, y usar un solo núcleo como techo de 100% pega el
                // círculo en rojo todo el tiempo con carga moderada).
                double cpuCapCores = Math.Max(1.0, Environment.ProcessorCount / 2.0);
                double raw = elapsedMs > 0 ? cpuMs / elapsedMs * 100.0 / cpuCapCores : 0.0;
                double cpuPct = Math.Clamp(raw, 0.0, 100.0);
                CircleCpu.SetCircle(cpuPct, $"CPU {raw:F1}%");
            }
            _lastCpuTime = totalCpu;
            _lastSampleTime = now;

            try
            {
                double gpu = GetGpuPercent(pids);
                CircleGpu.SetCircle(Math.Clamp(gpu, 0.0, 100.0), $"GPU {gpu:F1}%");
            }
            catch
            {
                CircleGpu.SetCircle(0, "GPU --");
            }
        }

        // Recorre Win32_Process una sola vez y arma el árbol para encontrar todos
        // los descendientes de rootPid (incluido él mismo).
        private static List<int> GetProcessTreePids(int rootPid)
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                    if (!childrenByParent.TryGetValue(ppid, out var list))
                    {
                        list = new List<int>();
                        childrenByParent[ppid] = list;
                    }
                    list.Add(pid);
                }
            }

            var pids = new List<int> { rootPid };
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (!childrenByParent.TryGetValue(current, out var kids)) continue;
                foreach (int kid in kids)
                {
                    if (pids.Contains(kid)) continue;
                    pids.Add(kid);
                    queue.Enqueue(kid);
                }
            }
            return pids;
        }

        // Win32_PerfRawData_GPUPerformanceCounters_GPUEngine expone el uso de GPU
        // por proceso (funciona con CUDA/Vulkan/DirectX). El campo "Name" tiene
        // formato pid_XXXX_luid_..._engtype_3D (o Compute); sumamos los engines
        // de todos los PIDs del árbol para obtener el total. Portado de
        // CONSULTOR-GUI/ProcessMonitorCircle.
        private double GetGpuPercent(List<int> pids)
        {
            ulong sumRunningTime = 0;
            ulong sumTimeStamp = 0;
            int count = 0;

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, RunningTime, TimeStamp_Sys100NS FROM Win32_PerfRawData_GPUPerformanceCounters_GPUEngine");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (!pids.Contains(ExtractPidFromName(name))) continue;

                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase))
                    continue;

                sumRunningTime += Convert.ToUInt64(obj["RunningTime"]);
                sumTimeStamp = Math.Max(sumTimeStamp, Convert.ToUInt64(obj["TimeStamp_Sys100NS"]));
                count++;
            }

            if (count == 0) return 0;

            double gpuPercent = 0;
            if (_lastGpuTimeStamp > 0 && sumTimeStamp > _lastGpuTimeStamp)
            {
                double deltaRunning = sumRunningTime - _lastGpuRaw;
                double deltaTimestamp = sumTimeStamp - _lastGpuTimeStamp;
                gpuPercent = Math.Clamp(deltaRunning / deltaTimestamp * 100.0, 0.0, 100.0);
            }
            _lastGpuRaw = sumRunningTime;
            _lastGpuTimeStamp = sumTimeStamp;
            return gpuPercent;
        }

        private static int ExtractPidFromName(string name)
        {
            const string prefix = "pid_";
            int start = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return -1;
            start += prefix.Length;
            int end = name.IndexOf('_', start);
            if (end < 0) end = name.Length;
            return int.TryParse(name[start..end], out int pid) ? pid : -1;
        }

        // ===================== Panel Log colapsable =====================

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            ToggleSectorColumna(ColLog, TxtLog, BtnToggleLog, ref _colLogGuardado);
        }

        // El Log ocupa una columna de ancho estrella compartida con el panel de
        // Resultados; al colapsar hay que recordar y restaurar ese ancho para
        // que Resultados vuelva a ocupar el espacio libre.
        private static void ToggleSectorColumna(ColumnDefinition columna, FrameworkElement contenido, Button boton, ref GridLength? guardado)
        {
            bool colapsar = contenido.Visibility == Visibility.Visible;
            if (colapsar)
            {
                guardado = columna.Width;
                contenido.Visibility = Visibility.Collapsed;
                columna.Width = GridLength.Auto;
                boton.Content = "▶";
            }
            else
            {
                contenido.Visibility = Visibility.Visible;
                columna.Width = guardado ?? new GridLength(1, GridUnitType.Star);
                boton.Content = "▼";
            }
        }

        // ===================== Actualizar (solo portable) =====================

        // La release de GitHub trae app\ en un zip; lo instala app\actualizar.py
        // (ver portable\actualizar.py). Acá solo se consulta si hay una versión
        // más nueva y, si el usuario acepta, se lanza una copia del
        // actualizador fuera de app\ y se cierra la ventana: el reemplazo
        // necesita la GUI, el backend y llama-server cerrados.
        private const string ApiUltimaRelease = "https://api.github.com/repos/elcoprofago/CONSULTOR-ACADEMICO/releases/latest";

        private static string VersionApp =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        private void InformarResultadoActualizacion()
        {
            if (!Entorno.EsPortable) return;
            string p = Path.Combine(Entorno.DataDir, "actualizacion.txt");
            try
            {
                if (!File.Exists(p)) return;
                string texto = File.ReadAllText(p, Encoding.UTF8).Trim();
                File.Delete(p);
                Log(texto, texto.StartsWith("OK") ? "OK" : "ERROR", protect: true);
            }
            catch (Exception ex)
            {
                Log($"No se pudo leer el resultado de la última actualización: {ex.Message}", "WARN");
            }
        }

        private async void BtnActualizar_Click(object sender, RoutedEventArgs e)
        {
            BtnActualizar.IsEnabled = false;
            try
            {
                string actual = VersionApp;
                Log("Buscando una versión nueva en GitHub...");
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiUltimaRelease);
                req.Headers.UserAgent.ParseAdd("CONSULTOR-ACADEMICO-actualizador");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var resp = await _http.SendAsync(req, cts.Token);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Log("Todavía no hay versiones publicadas en GitHub.", "WARN");
                    return;
                }
                resp.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string ultima = (json.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                if (!Version.TryParse(ultima, out var vUltima) || !Version.TryParse(actual, out var vActual))
                {
                    Log($"No se pudo comparar la versión publicada ({ultima}) con la instalada ({actual}).", "ERROR");
                    return;
                }
                if (vUltima <= vActual)
                {
                    Log($"Ya está la última versión ({actual}).", "OK");
                    MessageBox.Show(this, $"Ya tenés la última versión ({actual}).", "Actualizar",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show(this,
                        $"Hay una versión nueva: {ultima} (tenés la {actual}).\n\n" +
                        "Se cierra el consultor, se descarga e instala la nueva versión (solo cambia app\\; " +
                        "tus datos, el índice y los modelos no se tocan) y se vuelve a abrir.\n\n¿Actualizar ahora?",
                        "Actualizar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                // Copia fuera de app\: app\ se va a reemplazar entera.
                string tmp = Path.Combine(Path.GetTempPath(), "consultor_update_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tmp);
                string copia = Path.Combine(tmp, "actualizar.py");
                File.Copy(Path.Combine(Entorno.AppDir, "actualizar.py"), copia);
                var psi = new ProcessStartInfo(Entorno.Python)
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = Entorno.RaizPortable!,
                };
                foreach (string arg in new[] { "-I", copia, "--esperar-cierre", "--raiz", Entorno.RaizPortable! })
                    psi.ArgumentList.Add(arg);
                Process.Start(psi);
                Log($"Actualizando a {ultima}: el consultor se cierra y se vuelve a abrir solo.", "OK");
                Close();
            }
            catch (Exception ex)
            {
                Log($"No se pudo buscar o lanzar la actualización: {ex.Message}", "ERROR");
            }
            finally
            {
                BtnActualizar.IsEnabled = true;
            }
        }

        // ===================== Log desacoplado (otra ventana / otro monitor) =====================

        // El mismo TxtLog se mueve de PanelLog a una ventana aparte y vuelve al
        // cerrarla: Log() sigue escribiendo en él y no se pierde lo ya escrito.

        private void BtnDesacoplarLog_Click(object sender, RoutedEventArgs e) => DesacoplarLog();

        private void DesacoplarLog()
        {
            if (_ventanaLog != null) { _ventanaLog.Activate(); return; }

            if (TxtLog.Visibility != Visibility.Visible)
                ToggleSectorColumna(ColLog, TxtLog, BtnToggleLog, ref _colLogGuardado);

            _colLogAntesDeDesacoplar = ColLog.Width;
            PanelLog.Children.Remove(TxtLog);
            BorderLog.Visibility = Visibility.Collapsed;
            SplitterLog.Visibility = Visibility.Collapsed;
            ColSplitterLog.Width = new GridLength(0);
            ColLog.Width = new GridLength(0);

            var btnAcoplar = new Button
            {
                Content = "Acoplar",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Devolver el Log a la ventana principal",
            };
            var barra = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
            var titulo = new TextBlock { Text = "Log", Style = (Style)FindResource("SectionHeader") };
            DockPanel.SetDock(titulo, Dock.Left);
            DockPanel.SetDock(btnAcoplar, Dock.Right);
            barra.Children.Add(titulo);
            barra.Children.Add(btnAcoplar);

            var panel = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(barra, Dock.Top);
            panel.Children.Add(barra);
            panel.Children.Add(TxtLog);

            var ventana = new Window
            {
                Title = "Log — " + Title,
                Content = panel,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Owner = this,
                Icon = Icon,
                Width = 560,
                Height = 760,
                MinWidth = 300,
                MinHeight = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            UbicarVentanaLog(ventana, _settings.VentanaLog);

            btnAcoplar.Click += (_, _) => ventana.Close();
            ventana.Closing += (_, _) =>
            {
                // Al cerrar la app las ventanas propias se cierran solas: ahí
                // queda registrado que estaba desacoplado (Window_Closing).
                if (_cerrandoApp) return;
                _settings.VentanaLog = PosicionDe(ventana);
                _settings.LogDesacoplado = false;
                GuardarSettings();
            };
            ventana.Closed += (_, _) => AcoplarLog(panel);

            _ventanaLog = ventana;
            _settings.LogDesacoplado = true;
            GuardarSettings();
            ventana.Show();
            if (_settings.VentanaLog?.Maximizada == true) ventana.WindowState = WindowState.Maximized;
            TxtLog.ScrollToEnd();
        }

        private void AcoplarLog(DockPanel panelVentana)
        {
            panelVentana.Children.Remove(TxtLog);
            PanelLog.Children.Add(TxtLog);
            BorderLog.Visibility = Visibility.Visible;
            SplitterLog.Visibility = Visibility.Visible;
            ColSplitterLog.Width = new GridLength(6);
            ColLog.Width = _colLogAntesDeDesacoplar;
            _ventanaLog = null;
            TxtLog.ScrollToEnd();
        }

        // Tamaño inicial: el del XAML si entra en el área de trabajo del monitor
        // donde abre (el del cursor, como hacía CenterScreen); si no, el 90 % de
        // esa área, centrada. Los mínimos también se achican si no entran: con
        // MinHeight más alto que la pantalla, la barra de título quedaba afuera
        // y no había cómo mover ni cerrar la ventana.
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            var fuente = PresentationSource.FromVisual(this);
            if (fuente?.CompositionTarget == null) return;
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            Point arribaIzq = fuente.CompositionTarget.TransformFromDevice.Transform(new Point(area.Left, area.Top));
            Point abajoDer = fuente.CompositionTarget.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
            var trabajo = new Rect(arribaIzq, abajoDer);

            MinWidth = Math.Min(MinWidth, trabajo.Width);
            MinHeight = Math.Min(MinHeight, trabajo.Height);
            Width = Math.Max(MinWidth, Math.Min(Width, trabajo.Width * 0.9));
            Height = Math.Max(MinHeight, Math.Min(Height, trabajo.Height * 0.9));
            Left = trabajo.Left + (trabajo.Width - Width) / 2;
            Top = trabajo.Top + (trabajo.Height - Height) / 2;
        }

        private static PosicionVentana PosicionDe(Window w)
        {
            Rect r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
            return new PosicionVentana
            {
                Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height,
                Maximizada = w.WindowState == WindowState.Maximized,
            };
        }

        // Sólo se usa la posición guardada si cae dentro del escritorio actual:
        // el otro monitor puede no estar (o ser otra PC, desde el pendrive).
        private static void UbicarVentanaLog(Window ventana, PosicionVentana? pos)
        {
            if (pos == null || pos.Width < 200 || pos.Height < 150) return;
            var escritorio = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // La barra de título (unos 40 px de alto) tiene que quedar a la vista para poder moverla.
            var barraTitulo = new Rect(pos.Left, pos.Top, pos.Width, 40);
            barraTitulo.Intersect(escritorio);
            if (barraTitulo.IsEmpty || barraTitulo.Width < 100 || barraTitulo.Height < 20) return;

            ventana.WindowStartupLocation = WindowStartupLocation.Manual;
            ventana.Left = pos.Left;
            ventana.Top = pos.Top;
            ventana.Width = pos.Width;
            ventana.Height = pos.Height;
        }

        // ===================== Log =====================

        private void Log(string msg, string level = "INFO", bool overwrite = false, bool protect = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(msg, level, overwrite, protect)));
                return;
            }

            Brush color = level switch
            {
                "OK" => Brushes.LightGreen,
                "WARN" => Brushes.Khaki,
                "ERROR" => Brushes.OrangeRed,
                "SPINNER" => Brushes.Cyan,
                _ => Brushes.White,
            };

            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 14 };
            paragraph.Tag = protect ? "PROTECTED" : null;
            paragraph.Inlines.Add(new Run($"{DateTime.Now:HH:mm:ss} - {msg}") { Foreground = color });

            if (overwrite && TxtLog.Document.Blocks.LastBlock is Paragraph last && last.Tag?.ToString() != "PROTECTED")
                TxtLog.Document.Blocks.Remove(last);

            TxtLog.Document.Blocks.Add(paragraph);

            if (_autoScrollLog)
                TxtLog.ScrollToEnd();
        }

        // ===================== Cierre =====================

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _cerrandoApp = true;
            if (_ventanaLog != null)
            {
                _settings.VentanaLog = PosicionDe(_ventanaLog);
                _settings.LogDesacoplado = true;
                GuardarSettings();
            }

            _perfTimer?.Stop();

            try { if (_llamaProcess != null && !_llamaProcess.HasExited) _llamaProcess.Kill(true); } catch { }
            try { if (_backendProcess != null && !_backendProcess.HasExited) _backendProcess.Kill(true); } catch { }

            if (_settings.AutoPurgarAlCerrar)
            {
                // Los Kill(true) de arriba ya bajaron los procesos propios;
                // esto barre cualquier huérfano de instancias anteriores que
                // haya quedado corriendo (ver PurgarProcesosHuerfanos).
                try { PurgarProcesosHuerfanos(pedirConfirmacion: false); } catch { }
            }
        }
    }
}
