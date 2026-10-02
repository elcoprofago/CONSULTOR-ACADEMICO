using System.Windows;
using System.Windows.Input;

namespace ConsultorAcademicoGui
{
    // Elegir un registro del catálogo de CitaPDF: al agregar material (llena
    // los datos) o para vincular la ficha de un documento ya agregado.
    public partial class CatalogoCitaPdfWindow : Window
    {
        public class Fila
        {
            public FichaCitaPdf Ficha { get; init; } = new();
            public string Detalle { get; init; } = "";
            public string Aviso { get; init; } = "";
            public string Texto { get; init; } = "";  // normalizado, para buscar
            public int Parecido { get; init; }
        }

        public FichaCitaPdf? Elegida { get; private set; }

        private readonly List<Fila> _filas;

        // yaEnConsultor: hash de ficha -> documento_id del consultor que ya la
        // tiene (por mismo PDF o por vínculo), para avisarlo en la lista.
        // tituloReferencia: si no se escribe nada, la lista se ordena por
        // parecido con ese título.
        public CatalogoCitaPdfWindow(CatalogoLeido catalogo, string mensaje,
                                     IReadOnlyDictionary<string, string> yaEnConsultor, string? tituloReferencia)
        {
            InitializeComponent();
            TxtMensaje.Text = mensaje;
            TxtCatalogo.Text = $"{catalogo.Fichas.Count} registros · {catalogo.Ruta}";
            TxtCatalogo.ToolTip = catalogo.Ruta;

            var palabras = Palabras(tituloReferencia ?? "");
            _filas = catalogo.Fichas.Select(f =>
            {
                string texto = CatalogoCitaPdf.Normalizar(
                    $"{f.DocumentoId} {f.Titulo} {f.Autores} {f.Anio} {f.Editorial}");
                var detalle = new List<string>();
                if (f.AutoresApa.Count > 0) detalle.Add(f.Autores);
                if (!string.IsNullOrWhiteSpace(f.Anio)) detalle.Add(f.Anio);
                if (!string.IsNullOrWhiteSpace(f.Editorial)) detalle.Add(f.Editorial);
                return new Fila
                {
                    Ficha = f,
                    Detalle = string.Join(" · ", detalle),
                    Aviso = yaEnConsultor.TryGetValue(f.HashSha256, out var id) ? $"Ya está en el consultor como {id}" : "",
                    Texto = texto,
                    Parecido = palabras.Count(p => texto.Contains(p)),
                };
            }).ToList();

            Filtrar();
            Loaded += (_, _) => TxtBuscar.Focus();
        }

        private static List<string> Palabras(string s) =>
            CatalogoCitaPdf.Normalizar(s)
                .Split(new[] { ' ', ',', '.', ';', ':', '-', '(', ')', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length > 3)
                .Distinct()
                .ToList();

        private void Filtrar()
        {
            var palabras = CatalogoCitaPdf.Normalizar(TxtBuscar.Text)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<Fila> filas = _filas;
            filas = palabras.Length > 0
                ? filas.Where(f => palabras.All(p => f.Texto.Contains(p))).OrderBy(f => f.Ficha.DocumentoId)
                : filas.OrderByDescending(f => f.Parecido).ThenBy(f => f.Ficha.DocumentoId);
            ListFichas.ItemsSource = filas.ToList();
        }

        private void TxtBuscar_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Filtrar();

        private void ListFichas_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            BtnElegir.IsEnabled = ListFichas.SelectedItem != null;

        private void ListFichas_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListFichas.SelectedItem != null) BtnElegir_Click(sender, e);
        }

        private void BtnElegir_Click(object sender, RoutedEventArgs e)
        {
            if (ListFichas.SelectedItem is not Fila fila) return;
            Elegida = fila.Ficha;
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
            else if (e.Key == Key.Enter && ListFichas.SelectedItem != null) { e.Handled = true; BtnElegir_Click(sender, e); }
            else if (e.Key == Key.Down && TxtBuscar.IsKeyboardFocused && ListFichas.Items.Count > 0)
            {
                e.Handled = true;
                ListFichas.SelectedIndex = 0;
                (ListFichas.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            }
        }

        // Diccionario hash de ficha -> documento del consultor que ya la
        // tiene, para el aviso de la lista.
        public static Dictionary<string, string> YaEnConsultor(IEnumerable<CheckableItem> docs, CatalogoLeido catalogo)
        {
            var r = new Dictionary<string, string>();
            foreach (var d in docs)
            {
                var (ficha, _) = CatalogoCitaPdf.FichaDe(d, catalogo);
                if (ficha != null) r.TryAdd(ficha.HashSha256, d.Value);
            }
            return r;
        }
    }
}
