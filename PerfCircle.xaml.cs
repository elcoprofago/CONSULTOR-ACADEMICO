using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ConsultorAcademicoGui
{
    // Círculo de rendimiento embebido en la ventana principal (Requisito 2:
    // a diferencia de ProcessMonitorCircle original, no flota como ventana
    // aparte). Reutiliza literalmente los assets y la mecánica de crossfade
    // de MonitorWindow.xaml/.xaml.cs (ver Requisito 16 del spec).
    public partial class PerfCircle : UserControl
    {
        private readonly BitmapImage?[] _images = new BitmapImage?[11];
        private int _currentIdx = -1;

        public PerfCircle()
        {
            InitializeComponent();
            LoadImages();
        }

        public void SetSize(double size)
        {
            CircleGrid.Width = size;
            CircleGrid.Height = size;
            TxtMetric.FontSize = Math.Max(9, size / 7.0);
        }

        private void LoadImages()
        {
            for (int i = 1; i <= _images.Length; i++)
            {
                var uri = new Uri($"pack://application:,,,/Assets/circle_{i:D2}.png", UriKind.Absolute);
                try
                {
                    var sri = Application.GetResourceStream(uri);
                    if (sri == null) break;

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = sri.Stream;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    _images[i - 1] = bmp;
                }
                catch { break; }
            }

            ImgMarco.Source = new BitmapImage(
                new Uri("pack://application:,,,/Assets/marco.png", UriKind.Absolute));

            if (_images[0] != null)
            {
                ImgNew.Source = _images[0];
                _currentIdx = 0;
            }
        }

        public void SetCircle(double percent, string labelText)
        {
            int maxIdx = Array.FindLastIndex(_images, img => img != null);
            TxtMetric.Text = labelText;
            if (maxIdx < 0) return;

            int newIdx = PercentToIndex(percent, maxIdx);
            if (newIdx == _currentIdx) return;

            ImgOld.Source = _currentIdx >= 0 ? _images[_currentIdx] : null;
            ImgOld.Opacity = 1;
            ImgNew.Source = _images[newIdx];
            ImgNew.Opacity = 0;
            _currentIdx = newIdx;

            ((Storyboard)Resources["FadeIn"]).Begin(this);
            ((Storyboard)Resources["FadeOut"]).Begin(this);
        }

        // Los 9 assets están pensados como 3 tonos (bajo/medio/alto) de 3
        // matices cada uno, con los mismos cortes "semáforo" que
        // ProcessMonitorCircle: <30% / 30-69% / ≥70%. El mapeo lineal
        // anterior (percent/100*maxIdx) no respetaba esos cortes: cualquier
        // carga moderada (RAM cerca de la mitad del techo, CPU con varios
        // hilos activos) ya caía en los tonos rojos del final de la escala.
        private static int PercentToIndex(double percent, int maxIdx)
        {
            int totalImgs = maxIdx + 1;
            int perBand = Math.Max(1, totalImgs / 3);

            int band;
            double posInBand;
            if (percent < 30)
            {
                band = 0;
                posInBand = percent / 30.0;
            }
            else if (percent < 70)
            {
                band = 1;
                posInBand = (percent - 30) / 40.0;
            }
            else
            {
                band = 2;
                posInBand = (percent - 70) / 30.0;
            }

            int subIdx = (int)Math.Round(posInBand * (perBand - 1));
            subIdx = Math.Clamp(subIdx, 0, perBand - 1);
            return Math.Clamp(band * perBand + subIdx, 0, maxIdx);
        }
    }
}
