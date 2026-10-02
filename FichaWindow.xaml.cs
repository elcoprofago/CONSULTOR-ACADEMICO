using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;

namespace ConsultorAcademicoGui
{
    // Ficha de CitaPDF de un documento del consultor. Se muestra la del
    // mismo PDF (mismo SHA-256) o la vinculada a mano; vincular y quitar el
    // vínculo pasan por el backend (POST /documentos/{id}/ficha), que es
    // quien escribe documentos.json. biblioteca.json de CitaPDF no se toca.
    public partial class FichaWindow : Window
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(1) };

        private readonly CheckableItem _doc;
        private readonly List<CheckableItem> _todos;
        private readonly string? _rutaCatalogo;
        private readonly Action<string, string> _log;
        private FichaCitaPdf? _ficha;

        public bool HuboCambios { get; private set; }

        public FichaWindow(CheckableItem doc, List<CheckableItem> todos, string? rutaCatalogo, Action<string, string> log)
        {
            InitializeComponent();
            _doc = doc;
            _todos = todos;
            _rutaCatalogo = rutaCatalogo;
            _log = log;
            Mostrar();
        }

        private void Mostrar()
        {
            var catalogo = CatalogoCitaPdf.Cargar(_rutaCatalogo);
            var (ficha, tipo) = CatalogoCitaPdf.FichaDe(_doc, catalogo);
            _ficha = ficha;

            TxtDocumento.Text = $"{_doc.Value} — {_doc.Titulo}";
            TxtEstado.Text = !catalogo.Disponible
                ? catalogo.Error + (tipo == CatalogoCitaPdf.Vinculo.Manual ? " (El documento tiene un vínculo guardado, que se mostrará cuando el catálogo se pueda leer.)" : "")
                : tipo switch
                {
                    CatalogoCitaPdf.Vinculo.MismoPdf => "Ficha encontrada automáticamente: es el mismo PDF que está catalogado en CitaPDF.",
                    CatalogoCitaPdf.Vinculo.Manual => "Ficha vinculada a mano (el PDF del consultor no es el mismo archivo que el catalogado).",
                    CatalogoCitaPdf.Vinculo.ManualNoEncontrado => "El vínculo guardado apunta a un registro que ya no está en el catálogo de CitaPDF (se borró, o su PDF cambió). Podés vincular otro o quitarlo.",
                    _ => "Este documento no tiene ficha en el catálogo de CitaPDF: su PDF no está catalogado. Si el catálogo tiene otra edición o copia del mismo trabajo, podés vincularla.",
                };

            PanelFicha.Visibility = ficha != null ? Visibility.Visible : Visibility.Collapsed;
            BtnCopiarCita.Visibility = PanelFicha.Visibility;
            BtnVincular.IsEnabled = catalogo.Disponible && catalogo.Fichas.Count > 0;
            BtnVincular.Content = ficha != null ? "Vincular con otro registro..." : "Vincular con el catálogo...";
            BtnQuitar.Visibility = string.IsNullOrEmpty(_doc.FichaVinculada) ? Visibility.Collapsed : Visibility.Visible;
            if (ficha == null) return;

            TxtCita.Text = string.IsNullOrWhiteSpace(ficha.CitaApa) ? "(CitaPDF no tiene cita armada para este registro)" : ficha.CitaApa;
            GridCampos.Children.Clear();
            GridCampos.RowDefinitions.Clear();
            void Campo(string nombre, string? valor)
            {
                if (string.IsNullOrWhiteSpace(valor)) return;
                int fila = GridCampos.RowDefinitions.Count;
                GridCampos.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var n = new TextBlock { Text = nombre, Foreground = (System.Windows.Media.Brush)FindResource("TextDim"), FontSize = 11, Margin = new Thickness(0, 2, 10, 2) };
                var v = new TextBox { Text = valor, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetRow(n, fila); Grid.SetRow(v, fila); Grid.SetColumn(v, 1);
                GridCampos.Children.Add(n); GridCampos.Children.Add(v);
            }
            Campo("Registro", ficha.DocumentoId);
            Campo("Título", ficha.Titulo);
            Campo("Autores", ficha.Autores);
            Campo("Año", ficha.Anio);
            Campo("Editorial / fuente", ficha.Editorial);
            Campo("PDF catalogado", ficha.RutaArchivoOriginal);
            Campo("URL de origen", ficha.OrigenUrl);
            Campo("Catalogado el", ficha.FechaAdquisicion == default ? null : ficha.FechaAdquisicion.ToString("dd/MM/yyyy"));
        }

        private void BtnCopiarCita_Click(object sender, RoutedEventArgs e)
        {
            if (_ficha == null || string.IsNullOrWhiteSpace(_ficha.CitaApa)) return;
            Clipboard.SetText(_ficha.CitaApa);
            _log($"Cita copiada al portapapeles (ficha CitaPDF {_ficha.DocumentoId}): {_ficha.CitaApa}", "OK");
        }

        private async void BtnVincular_Click(object sender, RoutedEventArgs e)
        {
            var catalogo = CatalogoCitaPdf.Cargar(_rutaCatalogo);
            if (!catalogo.Disponible) { Mostrar(); return; }

            var yaEn = CatalogoCitaPdfWindow.YaEnConsultor(_todos.Where(d => d.Value != _doc.Value), catalogo);
            var wnd = new CatalogoCitaPdfWindow(catalogo,
                $"Elegí el registro de CitaPDF que corresponde a {_doc.Value} — «{_doc.Titulo}». La lista empieza por los títulos más parecidos.",
                yaEn, _doc.Titulo) { Owner = this };
            if (wnd.ShowDialog() != true || wnd.Elegida == null) return;
            var elegida = wnd.Elegida;

            if (yaEn.TryGetValue(elegida.HashSha256, out var otro) &&
                MessageBox.Show(this, $"Ese registro ya es la ficha de {otro}. ¿Vincularlo también a {_doc.Value}?",
                    "Ficha CitaPDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            if (await Guardar(elegida.HashSha256))
                _log($"Ficha de {_doc.Value} vinculada con CitaPDF {elegida.DocumentoId} — {elegida.Titulo}", "OK");
        }

        private async void BtnQuitar_Click(object sender, RoutedEventArgs e)
        {
            if (await Guardar(null))
                _log($"Se quitó el vínculo de {_doc.Value} con el catálogo de CitaPDF.", "OK");
        }

        private async Task<bool> Guardar(string? hashFicha)
        {
            IsEnabled = false;
            try
            {
                var resp = await Http.PostAsJsonAsync(
                    $"{MainWindow.BackendBaseUrl}/documentos/{Uri.EscapeDataString(_doc.Value)}/ficha",
                    new FichaRequest { ficha_citapdf = hashFicha });
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    string error = UbicarPdfWindow.ExtraerDetalle(body) ?? $"el backend respondió {(int)resp.StatusCode}";
                    MessageBox.Show(this, $"No se pudo guardar el vínculo: {error}", "Ficha CitaPDF",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
                _doc.FichaVinculada = hashFicha;
                HuboCambios = true;
                Mostrar();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo guardar el vínculo: {ex.Message}", "Ficha CitaPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
        }
    }
}
