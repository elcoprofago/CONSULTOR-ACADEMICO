using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ConsultorAcademicoGui
{
    public partial class SettingsWindow : Window
    {
        public AppSettings? Result { get; private set; }

        private readonly AppSettings _actual;

        // Resumen de la unificación para el log (null si no se unificó). Ya
        // quedó guardada aunque después se cancele esta ventana.
        public string? InformeUnificacion { get; private set; }

        public SettingsWindow(AppSettings actual, bool backendListo)
        {
            InitializeComponent();
            _actual = actual;
            if (!backendListo)
            {
                BtnUnificar.IsEnabled = false;
                LblUnificar.Text = "Para unificar con otra copia hay que esperar a que termine de cargar la biblioteca (el backend).";
            }

            switch (actual.Tema)
            {
                case "Claro": RbTemaClaro.IsChecked = true; break;
                case "Textura": RbTemaTextura.IsChecked = true; break;
                default: RbTemaOscuro.IsChecked = true; break;
            }
            TxtRutaTextura.Text = actual.RutaTextura ?? "";

            foreach (var item in CmbFormatoExportacion.Items)
            {
                if (item is System.Windows.Controls.ComboBoxItem cbi &&
                    (cbi.Content as string) == actual.FormatoExportacion)
                {
                    CmbFormatoExportacion.SelectedItem = cbi;
                    break;
                }
            }
            if (CmbFormatoExportacion.SelectedItem == null) CmbFormatoExportacion.SelectedIndex = 0;

            TxtCarpetaExportacion.Text = actual.CarpetaExportacionDefault ?? "";
            ChkUsarGpu.IsChecked = actual.UsarGpuBusqueda;
            TxtKDefault.Text = actual.KResultadosDefault?.ToString() ?? "5";
            SliderMmr.Value = actual.MmrLambdaDefault;
            SliderFuente.Value = actual.TamanoFuenteResultados;
            ChkAutoPurgar.IsChecked = actual.AutoPurgarAlCerrar;
            TxtCarpetaDescargas.Text = actual.CarpetaDescargas ?? "";
            _rutaCatalogo = actual.RutaCatalogoCitaPdf;
            MostrarCatalogo();
            _carpetaBiblioteca = actual.CarpetaBiblioteca;
            MostrarBiblioteca();
            _carpetaModelos = actual.CarpetaModelos;
            MostrarModelos();

            ActualizarEstadoTextura();
        }

        // null = el catálogo por defecto de CitaPDF instalado.
        private string? _rutaCatalogo;

        private void MostrarCatalogo()
        {
            var catalogo = CatalogoCitaPdf.Cargar(_rutaCatalogo);
            TxtCatalogoCitaPdf.Text = catalogo.Ruta;
            LblEstadoCatalogo.Text = (_rutaCatalogo == null ? "Por defecto. " : "") +
                (catalogo.Disponible ? $"Se lee bien: {catalogo.Fichas.Count} registros." : catalogo.Error);
        }

        private void BtnExaminarCatalogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Catálogo de CitaPDF (biblioteca.json)|*.json" };
            string actual = CatalogoCitaPdf.RutaEfectiva(_rutaCatalogo);
            string? carpeta = Path.GetDirectoryName(actual);
            if (carpeta != null && Directory.Exists(carpeta)) dlg.InitialDirectory = carpeta;
            if (dlg.ShowDialog() != true) return;
            _rutaCatalogo = dlg.FileName;
            MostrarCatalogo();
        }

        private void BtnUnificar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Elegí el documentos.json de la otra copia (no se va a modificar)",
                Filter = "Índice del consultor (documentos.json)|documentos.json|Todos los JSON (*.json)|*.json",
            };
            if (dlg.ShowDialog(this) != true) return;

            // Si es el mismo índice en uso, o no es un índice del consultor,
            // lo rechaza el backend y UnificarWindow muestra el motivo.
            var wnd = new UnificarWindow(dlg.FileName, UnificarWindow.BibliotecaDe(dlg.FileName)) { Owner = this };
            if (wnd.ShowDialog() == true && wnd.Informe != null) InformeUnificacion = wnd.Informe;
        }

        private void BtnCatalogoPorDefecto_Click(object sender, RoutedEventArgs e)
        {
            _rutaCatalogo = null;
            MostrarCatalogo();
        }

        // null = automática (ver Entorno.CarpetaBiblioteca / CarpetasModelos).
        private string? _carpetaBiblioteca;
        private string? _carpetaModelos;

        private void MostrarBiblioteca()
        {
            string? efectiva = Entorno.CarpetaBiblioteca(_carpetaBiblioteca);
            TxtCarpetaBiblioteca.Text = efectiva ?? "";
            LblEstadoBiblioteca.Text =
                (_carpetaBiblioteca == null ? "Automática. " : "") +
                (efectiva == null
                    ? "Ninguna: las rutas de los PDF se guardan completas."
                    : Directory.Exists(efectiva)
                        ? "Las rutas de los PDF de adentro se guardan relativas a ella: siguen valiendo si la unidad cambia de letra."
                        : "La carpeta no existe.");
        }

        private void MostrarModelos()
        {
            TxtCarpetaModelos.Text = _carpetaModelos ?? "";
            string? llm = Entorno.BuscarModeloLlm(_carpetaModelos);
            string? emb = Entorno.BuscarModeloEmbeddings(_carpetaModelos);
            LblEstadoModelos.Text =
                $"Se busca en: {string.Join(", ", Entorno.CarpetasModelos(_carpetaModelos))}.\n" +
                $"{Entorno.ModeloLlm}: {llm ?? "no encontrado"}\n" +
                $"{Entorno.ModeloEmbeddings}: {emb ?? "no encontrado (se usa la caché de HuggingFace de este usuario)"}";
        }

        private static string? ElegirCarpeta(string? inicial)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (!string.IsNullOrEmpty(inicial) && Directory.Exists(inicial)) dlg.SelectedPath = inicial;
            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        private void BtnExaminarBiblioteca_Click(object sender, RoutedEventArgs e)
        {
            string? elegida = ElegirCarpeta(Entorno.CarpetaBiblioteca(_carpetaBiblioteca));
            if (elegida == null) return;
            _carpetaBiblioteca = elegida;
            MostrarBiblioteca();
        }

        private void BtnBibliotecaAutomatica_Click(object sender, RoutedEventArgs e)
        {
            _carpetaBiblioteca = null;
            MostrarBiblioteca();
        }

        private void BtnExaminarModelos_Click(object sender, RoutedEventArgs e)
        {
            string? elegida = ElegirCarpeta(_carpetaModelos);
            if (elegida == null) return;
            _carpetaModelos = elegida;
            MostrarModelos();
        }

        private void BtnModelosAutomatica_Click(object sender, RoutedEventArgs e)
        {
            _carpetaModelos = null;
            MostrarModelos();
        }

        private void Tema_Changed(object sender, RoutedEventArgs e) => ActualizarEstadoTextura();

        private void ActualizarEstadoTextura()
        {
            // BtnExaminarTextura puede no estar inicializado todavía la primera
            // vez que el RadioButton.Checked inicial dispara este handler desde
            // InitializeComponent().
            if (BtnExaminarTextura == null) return;
            bool habilitado = RbTemaTextura?.IsChecked == true;
            BtnExaminarTextura.IsEnabled = habilitado;
            TxtRutaTextura.IsEnabled = habilitado;
        }

        private void BtnExaminarTextura_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (dlg.ShowDialog() == true) TxtRutaTextura.Text = dlg.FileName;
        }

        private void BtnExaminarCarpeta_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (!string.IsNullOrEmpty(TxtCarpetaExportacion.Text) && Directory.Exists(TxtCarpetaExportacion.Text))
                dlg.SelectedPath = TxtCarpetaExportacion.Text;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtCarpetaExportacion.Text = dlg.SelectedPath;
        }

        private void BtnExaminarDescargas_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (!string.IsNullOrEmpty(TxtCarpetaDescargas.Text) && Directory.Exists(TxtCarpetaDescargas.Text))
                dlg.SelectedPath = TxtCarpetaDescargas.Text;

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtCarpetaDescargas.Text = dlg.SelectedPath;
        }

        private void SliderMmr_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblMmr != null) LblMmr.Text = SliderMmr.Value.ToString("F1");
        }

        private void SliderFuente_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblFuente != null) LblFuente.Text = SliderFuente.Value.ToString("F0");
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            string tema = RbTemaClaro.IsChecked == true ? "Claro"
                : RbTemaTextura.IsChecked == true ? "Textura"
                : "Oscuro";

            if (tema == "Textura" && (string.IsNullOrWhiteSpace(TxtRutaTextura.Text) || !File.Exists(TxtRutaTextura.Text)))
            {
                MessageBox.Show(this, "Elegí una imagen válida para el fondo con textura.", "Configuración",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtKDefault.Text, out int kDefault) || kDefault <= 0)
            {
                MessageBox.Show(this, "El valor de K (resultados) por defecto debe ser un número entero positivo.",
                    "Configuración", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string formato = (CmbFormatoExportacion.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "docx";

            // Se parte de una copia de la configuración actual y se pisan sólo
            // los campos de esta ventana: armando un AppSettings nuevo, cada
            // campo que no se edita acá (historial de búsquedas, Log
            // desacoplado) se borraba al guardar si no se lo copiaba a mano.
            var r = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                System.Text.Json.JsonSerializer.Serialize(_actual))!;
            r.Tema = tema;
            r.RutaTextura = tema == "Textura" ? TxtRutaTextura.Text.Trim() : null;
            r.FormatoExportacion = formato;
            r.CarpetaExportacionDefault = string.IsNullOrWhiteSpace(TxtCarpetaExportacion.Text) ? null : TxtCarpetaExportacion.Text.Trim();
            r.UsarGpuBusqueda = ChkUsarGpu.IsChecked == true;
            r.TamanoFuenteResultados = SliderFuente.Value;
            r.KResultadosDefault = kDefault;
            r.MmrLambdaDefault = SliderMmr.Value;
            r.AutoPurgarAlCerrar = ChkAutoPurgar.IsChecked == true;
            r.CarpetaDescargas = string.IsNullOrWhiteSpace(TxtCarpetaDescargas.Text) ? null : TxtCarpetaDescargas.Text.Trim();
            r.RutaCatalogoCitaPdf = _rutaCatalogo;
            r.CarpetaBiblioteca = _carpetaBiblioteca;
            r.CarpetaModelos = _carpetaModelos;
            Result = r;
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
