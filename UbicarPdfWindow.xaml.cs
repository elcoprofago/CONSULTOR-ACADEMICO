using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;

namespace ConsultorAcademicoGui
{
    // Indica dónde está ahora el PDF de un documento: eligiendo el archivo a
    // mano o buscándolo en una carpeta por su contenido. Adaptada de CitaPDF
    // (UbicarPdfWindow), con dos diferencias: la ruta se guarda a través del
    // backend (POST /documentos/{id}/ruta, que es quien escribe
    // documentos.json), y un archivo con otro contenido se rechaza en vez de
    // adoptarse, porque los fragmentos indexados citan las páginas del PDF
    // original.
    public partial class UbicarPdfWindow : Window
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(1) };
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private readonly CheckableItem _doc;
        private readonly List<CheckableItem> _todos;
        private CancellationTokenSource? _cts;
        private bool _cerrada;

        public UbicarPdfWindow(CheckableItem doc, List<CheckableItem> todos)
        {
            InitializeComponent();
            _doc = doc;
            _todos = todos;

            string titulo = $"«{doc.Titulo}» ({doc.Value})";
            if (string.IsNullOrWhiteSpace(doc.RutaArchivo))
                TxtMensaje.Text = $"{titulo} se agregó cuando el consultor todavía copiaba los PDFs, y no tiene registrada la ubicación del original. Indicá dónde está:";
            else if (!File.Exists(doc.RutaArchivo))
                TxtMensaje.Text = $"No se encontró el PDF de {titulo} en la ruta guardada. Si lo moviste o renombraste, indicá dónde está ahora:";
            else
                TxtMensaje.Text = $"Ubicación actual del PDF de {titulo}:";
            TxtRuta.Text = doc.RutaArchivo ?? "";

            if (!string.IsNullOrWhiteSpace(doc.CopiaInterna) && File.Exists(doc.CopiaInterna))
                BtnCopiaInterna.Visibility = Visibility.Visible;

            if (string.IsNullOrWhiteSpace(doc.Hash))
            {
                BtnBuscar.IsEnabled = false;
                BtnBuscar.ToolTip = "Este documento no tiene la huella del PDF: sólo se puede elegir el archivo a mano.";
            }
        }

        // Botón "PDF" de la lista de documentos: abre el original si está en
        // la ruta registrada; si no, ofrece ubicarlo. Devuelve true si se
        // registró alguna ruta nueva (el llamador recarga la lista).
        public static bool AbrirDocumento(Window owner, CheckableItem doc, List<CheckableItem> todos)
        {
            try
            {
                if (!Ubicador.FaltaArchivo(doc))
                {
                    Process.Start(new ProcessStartInfo(doc.RutaArchivo!) { UseShellExecute = true });
                    return false;
                }
                var ventana = new UbicarPdfWindow(doc, todos) { Owner = owner };
                bool ok = ventana.ShowDialog() == true;
                if (ok && File.Exists(doc.RutaArchivo))
                    Process.Start(new ProcessStartInfo(doc.RutaArchivo!) { UseShellExecute = true });
                return ok || ventana._huboCambios;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "No se pudo abrir: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool _huboCambios;

        private void BtnCopiaInterna_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_doc.CopiaInterna!) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir: " + ex.Message, "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnElegir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elegir el PDF de este documento",
                Filter = "PDF (*.pdf)|*.pdf|Todos los archivos (*.*)|*.*",
                InitialDirectory = Ubicador.CarpetaExistenteMasCercana(_doc.RutaArchivo) ?? "",
            };
            if (dlg.ShowDialog(this) != true) return;
            await Asociar(dlg.FileName);
        }

        // Ruta corregida a mano: muchas veces alcanza con cambiar la letra de
        // la unidad. Las comillas se quitan porque "Copiar como ruta" del
        // Explorador las agrega.
        private async void BtnUsarRuta_Click(object sender, RoutedEventArgs e)
        {
            string ruta = TxtRuta.Text.Trim().Trim('"').Trim();
            if (ruta.Length == 0)
            {
                MessageBox.Show(this, "Escribí la ruta del PDF.", "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!File.Exists(ruta))
            {
                MessageBox.Show(this, $"No existe el archivo:\n{ruta}", "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await Asociar(Path.GetFullPath(ruta));
        }

        private void TxtRuta_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            BtnUsarRuta_Click(sender, e);
        }

        // Asocia 'ruta' al documento comprobando antes su contenido contra la
        // huella indexada (común a elegir el archivo y escribir la ruta). El
        // backend lo vuelve a comprobar; esto sólo da un mensaje más claro.
        private async Task Asociar(string ruta)
        {
            string hash;
            try { hash = await Task.Run(() => Ubicador.CalcularHash(ruta)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo leer el archivo: " + ex.Message, "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_doc.Hash) && !string.Equals(hash, _doc.Hash, StringComparison.OrdinalIgnoreCase))
            {
                var otro = _todos.FirstOrDefault(d => !ReferenceEquals(d, _doc) &&
                    string.Equals(d.Hash, hash, StringComparison.OrdinalIgnoreCase));
                string msg = otro != null
                    ? $"Ese PDF es otro documento de la biblioteca: {otro.Value} ({otro.Titulo}). Elegí el archivo de este documento."
                    : "El archivo elegido no tiene el mismo contenido que el PDF que se indexó (puede ser otra edición o una copia modificada).\n\n" +
                      "No se puede asociar: los fragmentos ya indexados citan las páginas del original. Si es la versión que querés usar, agregala como material nuevo.";
                MessageBox.Show(this, msg, "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (await Guardar(new[] { (_doc, ruta) }) == 1) DialogResult = true;
        }

        private async void BtnBuscar_Click(object sender, RoutedEventArgs e)
        {
            string carpeta;
            using (var fb = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Carpeta donde buscar el PDF (se revisan también sus subcarpetas)",
                UseDescriptionForTitle = true,
                SelectedPath = Ubicador.CarpetaExistenteMasCercana(_doc.RutaArchivo) ?? "",
            })
            {
                if (fb.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                carpeta = fb.SelectedPath;
            }

            // Una sola recorrida sirve para todos los que falten: los demás
            // documentos cuyo PDF tampoco está en su ruta se buscan a la vez.
            var faltantes = _todos.Where(d => !string.IsNullOrWhiteSpace(d.Hash) &&
                (ReferenceEquals(d, _doc) || Ubicador.FaltaArchivo(d))).ToList();
            var buscados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in faltantes)
                buscados.TryAdd(d.Hash, Path.GetFileName(d.RutaArchivo ?? ""));

            Dictionary<string, string> hallados;
            _cts = new CancellationTokenSource();
            EnBusqueda(true);
            try
            {
                var progreso = new Progress<string>(t => TxtProgreso.Text = t);
                var token = _cts.Token;
                hallados = await Task.Run(() => Ubicador.Buscar(carpeta, buscados, progreso, token));
            }
            catch (OperationCanceledException)
            {
                if (!_cerrada) TxtProgreso.Text = "Búsqueda cancelada.";
                return;
            }
            catch (Exception ex)
            {
                TxtProgreso.Text = "No se pudo recorrer la carpeta: " + ex.Message;
                return;
            }
            finally
            {
                if (!_cerrada) EnBusqueda(false);
                _cts.Dispose();
                _cts = null;
            }

            // Se cerró la ventana mientras buscaba: no se aplica nada.
            if (_cerrada) return;

            bool esteHallado = hallados.TryGetValue(_doc.Hash, out var rutaEste);
            var otros = faltantes.Where(d => !ReferenceEquals(d, _doc) && hallados.ContainsKey(d.Hash)).ToList();

            var cambios = new List<(CheckableItem, string)>();
            if (esteHallado) cambios.Add((_doc, rutaEste!));
            if (otros.Count > 0)
            {
                string lista = string.Join("\n", otros.Take(10).Select(d => $"  {d.Value}  {d.Titulo}"));
                if (otros.Count > 10) lista += $"\n  ... y {otros.Count - 10} más";
                var r = MessageBox.Show(this,
                    $"{(esteHallado ? "Además, se" : "No se encontró este PDF, pero se")} encontraron en esa carpeta {otros.Count} documento(s) más sin PDF ubicado:\n\n{lista}\n\n¿Registrar también sus rutas?",
                    "Ubicar PDF", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                    cambios.AddRange(otros.Select(d => (d, hallados[d.Hash])));
            }

            int guardados = cambios.Count > 0 ? await Guardar(cambios) : 0;
            if (esteHallado && _doc.RutaArchivo == rutaEste)
            {
                DialogResult = true;
                return;
            }
            TxtProgreso.Text = (esteHallado ? "" : $"No se encontró este PDF en {carpeta}.") +
                (guardados > 0 ? $" Se registraron las rutas de {guardados} documento(s)." : "") +
                (esteHallado ? "" : " Podés probar con otra carpeta o elegir el archivo a mano.");
            TxtProgreso.Visibility = Visibility.Visible;
        }

        // Registra cada ruta en el backend; devuelve cuántas se guardaron.
        // Las que fallan se informan juntas al final y no se tocan.
        private async Task<int> Guardar(IEnumerable<(CheckableItem Doc, string Ruta)> cambios)
        {
            int ok = 0;
            var errores = new List<string>();
            IsEnabled = false;
            try
            {
                foreach (var (doc, ruta) in cambios)
                {
                    try
                    {
                        var resp = await Http.PostAsJsonAsync(
                            $"{MainWindow.BackendBaseUrl}/documentos/{Uri.EscapeDataString(doc.Value)}/ruta",
                            new RutaRequest { ruta_archivo = ruta });
                        string body = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            var actualizado = JsonSerializer.Deserialize<DocumentoDto>(body, JsonOpts);
                            doc.RutaArchivo = actualizado?.ruta_archivo ?? ruta;
                            doc.CopiaInterna = actualizado?.copia_interna;
                            _huboCambios = true;
                            ok++;
                        }
                        else
                        {
                            errores.Add($"{doc.Value}: {ExtraerDetalle(body) ?? $"el backend respondió {(int)resp.StatusCode}"}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errores.Add($"{doc.Value}: {ex.Message}");
                    }
                }
            }
            finally
            {
                IsEnabled = true;
            }

            if (errores.Count > 0)
                MessageBox.Show(this, "No se pudo registrar la ruta de:\n\n" + string.Join("\n", errores),
                    "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return ok;
        }

        // 400/404 traen {"detail": "texto"}; 409 trae {"detail": {"mensaje": ...}}.
        internal static string? ExtraerDetalle(string body)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("detail", out var detail)) return null;
                if (detail.ValueKind == JsonValueKind.String) return detail.GetString();
                if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("mensaje", out var m)) return m.GetString();
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void EnBusqueda(bool buscando)
        {
            BtnElegir.IsEnabled = !buscando;
            BtnUsarRuta.IsEnabled = !buscando;
            BtnCopiaInterna.IsEnabled = !buscando;
            TxtRuta.IsReadOnly = buscando;
            BtnBuscar.IsEnabled = !buscando && !string.IsNullOrWhiteSpace(_doc.Hash);
            BtnCancelar.Content = buscando ? "Detener búsqueda" : "Cancelar";
            TxtProgreso.Visibility = Visibility.Visible;
            if (buscando) TxtProgreso.Text = "Listando PDFs...";
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); return; }
            Close();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _cerrada = true;
            _cts?.Cancel();
        }

        // Escape: detiene la búsqueda si hay una en curso; si no, cierra.
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            BtnCancelar_Click(sender, e);
        }
    }
}
