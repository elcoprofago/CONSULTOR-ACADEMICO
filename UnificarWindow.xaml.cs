using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace ConsultorAcademicoGui
{
    // Suma a esta biblioteca lo que tenga otra copia del consultor (la de la
    // PC, la del pendrive, otra PC), con los criterios del Unificador de
    // CitaPDF: se empareja por SHA-256 del PDF, lo vacío acá se completa con
    // lo de allá, lo distinto en los dos lo decide el usuario, y lo que está
    // sólo allá se agrega. El trabajo lo hace el backend
    // (ACADEMICO-SCRIPTS/unificar.py): acá sólo se muestra y se elige.
    public partial class UnificarWindow : Window
    {
        // Agregar vectores calculados con otro modelo obliga a recalcularlos:
        // con miles de fragmentos y sin GPU puede llevar bastante.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };

        public class ConflictoVista
        {
            private readonly ConflictoDto _c;
            public ConflictoVista(ConflictoDto c)
            {
                _c = c;
                ElegirOtraVersion = c.sugerencia_es_otro;
                string titulo = string.IsNullOrWhiteSpace(c.local.titulo) ? c.otro.titulo : c.local.titulo;
                Encabezado = c.local.documento_id == c.otro.documento_id
                    ? $"{c.local.documento_id} — {titulo}"
                    : $"{c.local.documento_id} (en la otra: {c.otro.documento_id}) — {titulo}";
                TextoLocal = Describir("Esta biblioteca", c.local, !c.sugerencia_es_otro, x => x.local);
                TextoOtro = Describir("La otra", c.otro, c.sugerencia_es_otro, x => x.otro);
            }

            public string DocumentoIdLocal => _c.local.documento_id;
            public bool ElegirOtraVersion { get; private set; }
            public string Encabezado { get; }
            public string TextoLocal { get; }
            public string TextoOtro { get; }

            // El RadioButton que se desmarca empuja false: se ignora, manda
            // el que se marca.
            public bool ElegirLocal { get => !ElegirOtraVersion; set { if (value) ElegirOtraVersion = false; } }
            public bool ElegirOtro { get => ElegirOtraVersion; set { if (value) ElegirOtraVersion = true; } }

            private string Describir(string nombre, VistaDocDto d, bool sugerida, Func<CampoConflictoDto, string> valor)
            {
                var lineas = new List<string>
                {
                    nombre + (sugerida ? "  (sugerida)" : ""),
                    "Modificado: " + (Fecha(d.fecha_modificacion) ?? $"sin registro (ingresado el {Fecha(d.fecha_ingesta, "dd/MM/yyyy") ?? "?"})"),
                    $"Datos: {d.datos} caracteres",
                };
                foreach (var campo in _c.campos)
                    lineas.Add($"{campo.campo}: {(string.IsNullOrEmpty(valor(campo)) ? "(vacío)" : valor(campo))}");
                return string.Join("\n", lineas);
            }
        }

        public class AgregadoVista
        {
            private readonly VistaDocDto _d;
            public AgregadoVista(VistaDocDto d)
            {
                _d = d;
                Incluir = Agregable;
            }

            public string DocumentoId => _d.documento_id;
            public bool Agregable => _d.motivo_no_agregable == null;
            public bool Incluir { get; set; }
            public string Texto =>
                $"{_d.documento_id} — {(string.IsNullOrWhiteSpace(_d.titulo) ? "(sin título)" : _d.titulo)}" +
                (string.IsNullOrWhiteSpace(_d.autor) ? "" : $" — {_d.autor}") +
                (string.IsNullOrWhiteSpace(_d.anio) ? "" : $" ({_d.anio})") +
                $"  ·  {_d.n_fragmentos} fragmentos" +
                (Agregable ? "" : $"\nNo se puede agregar: {_d.motivo_no_agregable}.");
        }

        private readonly UnificarRequest _pedido;
        private List<ConflictoVista> _conflictos = new();
        private List<AgregadoVista> _agregados = new();

        // Resumen para el log de MainWindow (null si no se unificó).
        public string? Informe { get; private set; }

        public UnificarWindow(string rutaOtra, string? bibliotecaOtra)
        {
            InitializeComponent();
            _pedido = new UnificarRequest { otra = rutaOtra, biblioteca_otra = bibliotecaOtra };
        }

        // La carpeta de biblioteca de la otra copia, para resolver las rutas
        // relativas de sus PDF: la elegida en su configuración (data\config.json)
        // o, si es portable, <su unidad>\BASE, igual que
        // Entorno.CarpetaBiblioteca para la copia en uso. null si no se sabe:
        // el backend avisa por cada ruta relativa que no pudo resolver.
        public static string? BibliotecaDe(string rutaDocumentos)
        {
            try
            {
                // <data>\ACADEMICO-INDEX\documentos.json
                string? data = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rutaDocumentos)));
                if (data == null) return null;
                string config = Path.Combine(data, "config.json");
                if (File.Exists(config))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(config));
                    if (!string.IsNullOrWhiteSpace(s?.CarpetaBiblioteca)) return s!.CarpetaBiblioteca!.Trim();
                }
                string? raiz = Path.GetDirectoryName(data);
                if (raiz != null && File.Exists(Path.Combine(raiz, "portable.flag")))
                {
                    string porDefecto = Path.Combine(Path.GetPathRoot(raiz)!, "BASE");
                    if (Directory.Exists(porDefecto)) return porDefecto;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string? Fecha(string? iso, string formato = "dd/MM/yyyy HH:mm") =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f.ToString(formato) : null;

        private static string InfoBase(InfoBaseDto b) =>
            $"{b.ruta}\nModificada: {Fecha(b.modificado) ?? "?"}  ·  Tamaño: {b.bytes / 1048576.0:0.0} MB\n" +
            $"{b.documentos} documentos  ·  {b.fragmentos} fragmentos";

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AnalisisUnificacionDto? a;
            try
            {
                a = await Pedir<AnalisisUnificacionDto>("analizar", _pedido);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo comparar con la otra biblioteca:\n{ex.Message}", "Unificar bibliotecas",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            if (a == null) { Close(); return; }

            TxtInfoLocal.Text = InfoBase(a.esta);
            TxtInfoOtra.Text = InfoBase(a.otra);

            _conflictos = a.conflictos.Select(c => new ConflictoVista(c)).ToList();
            _agregados = a.agregados.Select(d => new AgregadoVista(d)).ToList();
            var completados = a.completados.Select(p =>
                $"{p.local.documento_id}" + (p.local.documento_id == p.otro.documento_id ? "" : $" (en la otra: {p.otro.documento_id})") +
                $" — {p.local.titulo}: {string.Join(", ", p.campos)}").ToList();

            ListaConflictos.ItemsSource = _conflictos;
            ListaAgregados.ItemsSource = _agregados;
            ListaCompletados.ItemsSource = completados;
            ExpConflictos.Header = $"Conflictos a resolver ({_conflictos.Count})";
            ExpAgregados.Header = $"Sólo en la otra: se agregan ({_agregados.Count})";
            ExpCompletados.Header = $"Se completan campos vacíos ({completados.Count})";
            ExpConflictos.Visibility = _conflictos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExpAgregados.Visibility = _agregados.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExpCompletados.Visibility = completados.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            int noAgregables = _agregados.Count(x => !x.Agregable);
            TxtResumen.Text =
                $"Iguales en ambas: {a.identicos}  ·  Sólo en ésta (se conservan): {a.solo_locales}  ·  " +
                $"Sólo en la otra: {_agregados.Count}" + (noAgregables > 0 ? $" ({noAgregables} no se pueden agregar)" : "") +
                $"  ·  Con campos a completar: {completados.Count}  ·  Conflictos: {_conflictos.Count}";

            if (_conflictos.Count == 0 && _agregados.Count == noAgregables && completados.Count == 0)
                TxtResumen.Text += "\n\nNo hay nada para unificar: esta biblioteca ya contiene todo lo de la otra.";
            else
                BtnUnificar.IsEnabled = true;
        }

        private async void BtnUnificar_Click(object sender, RoutedEventArgs e)
        {
            _pedido.elegir_otra = _conflictos.Where(c => c.ElegirOtraVersion).Select(c => c.DocumentoIdLocal).ToList();
            _pedido.excluir = _agregados.Where(x => x.Agregable && !x.Incluir).Select(x => x.DocumentoId).ToList();

            string resumenAntes = TxtResumen.Text;
            IsEnabled = false;
            TxtResumen.Text = "Unificando... (si los vectores de la otra son de otro modelo hay que recalcularlos, y puede tardar varios minutos)";
            try
            {
                var r = await Pedir<ResultadoUnificacionDto>("aplicar", _pedido);
                Informe = r?.informe;
                if (Informe != null)
                    MessageBox.Show(this, Informe, "Unificación terminada", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                TxtResumen.Text = resumenAntes;
                MessageBox.Show(this, $"No se pudo unificar:\n{ex.Message}\n\nEsta biblioteca quedó como estaba.", "Unificar bibliotecas",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private static async Task<T?> Pedir<T>(string accion, UnificarRequest pedido)
        {
            var resp = await Http.PostAsJsonAsync($"{MainWindow.BackendBaseUrl}/unificar/{accion}", pedido);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(UbicarPdfWindow.ExtraerDetalle(body) ?? $"el backend respondió {(int)resp.StatusCode}");
            return JsonSerializer.Deserialize<T>(body);
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && IsEnabled) { e.Handled = true; Close(); }
        }

        // Como MainWindow.Window_SourceInitialized: que entre en el monitor
        // de la ventana dueña, centrada sobre ella.
        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            var fuente = PresentationSource.FromVisual(this);
            if (fuente?.CompositionTarget == null || Owner == null) return;
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(Owner).Handle).WorkingArea;
            Point arribaIzq = fuente.CompositionTarget.TransformFromDevice.Transform(new Point(area.Left, area.Top));
            Point abajoDer = fuente.CompositionTarget.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
            var trabajo = new Rect(arribaIzq, abajoDer);

            MinWidth = Math.Min(MinWidth, trabajo.Width);
            MinHeight = Math.Min(MinHeight, trabajo.Height);
            Width = Math.Max(MinWidth, Math.Min(Width, trabajo.Width * 0.9));
            Height = Math.Max(MinHeight, Math.Min(Height, trabajo.Height * 0.9));
            Left = Math.Clamp(Owner.Left + (Owner.ActualWidth - Width) / 2, trabajo.Left, trabajo.Right - Width);
            Top = Math.Clamp(Owner.Top + (Owner.ActualHeight - Height) / 2, trabajo.Top, trabajo.Bottom - Height);
        }
    }
}
