using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ConsultorAcademicoGui
{
    public partial class SettingsWindow : Window
    {
        public AppSettings? Result { get; private set; }

        private readonly AppSettings _actual;

        public SettingsWindow(AppSettings actual)
        {
            InitializeComponent();
            _actual = actual;

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

        private void BtnCatalogoPorDefecto_Click(object sender, RoutedEventArgs e)
        {
            _rutaCatalogo = null;
            MostrarCatalogo();
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

            Result = new AppSettings
            {
                Tema = tema,
                RutaTextura = tema == "Textura" ? TxtRutaTextura.Text.Trim() : null,
                FormatoExportacion = formato,
                CarpetaExportacionDefault = string.IsNullOrWhiteSpace(TxtCarpetaExportacion.Text) ? null : TxtCarpetaExportacion.Text.Trim(),
                UsarGpuBusqueda = ChkUsarGpu.IsChecked == true,
                TamanoFuenteResultados = SliderFuente.Value,
                KResultadosDefault = kDefault,
                MmrLambdaDefault = SliderMmr.Value,
                AutoPurgarAlCerrar = ChkAutoPurgar.IsChecked == true,
                CarpetaDescargas = string.IsNullOrWhiteSpace(TxtCarpetaDescargas.Text) ? null : TxtCarpetaDescargas.Text.Trim(),
                RutaCatalogoCitaPdf = _rutaCatalogo,
                // No se edita en esta ventana: sin copiarlo, guardar la
                // configuración borraba el historial de búsquedas.
                HistorialBusquedas = _actual.HistorialBusquedas,
            };
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
