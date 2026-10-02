using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace ConsultorAcademicoGui
{
    public partial class AgregarMaterialWindow : Window
    {
        private readonly Action<string, string, bool, bool> _log;
        private readonly Func<Task> _refrescarDocumentos;
        private readonly string? _carpetaDescargas;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        public AgregarMaterialWindow(Action<string, string, bool, bool> log, Func<Task> refrescarDocumentos,
                                     string? carpetaDescargas)
        {
            InitializeComponent();
            _log = log;
            _refrescarDocumentos = refrescarDocumentos;
            _carpetaDescargas = string.IsNullOrWhiteSpace(carpetaDescargas) ? null : carpetaDescargas;
            ActualizarModoOrigen();
        }

        private bool CarpetaDescargasValida =>
            _carpetaDescargas != null && Directory.Exists(_carpetaDescargas);

        private void RbOrigen_Changed(object sender, RoutedEventArgs e) => ActualizarModoOrigen();

        private void ActualizarModoOrigen()
        {
            if (PanelArchivo == null || TxtUrl == null) return;
            bool esArchivo = RbOrigenArchivo.IsChecked == true;
            PanelArchivo.Visibility = esArchivo ? Visibility.Visible : Visibility.Collapsed;
            TxtUrl.Visibility = esArchivo ? Visibility.Collapsed : Visibility.Visible;
            // El consultor no copia PDFs: el archivo local se indexa desde
            // donde está, y el descargado queda en la carpeta de descargas.
            LblNotaOrigen.Text = esArchivo
                ? "El PDF se indexa desde su ubicación actual, sin copiarlo. Si después lo movés o lo renombrás, el botón \"PDF\" de la lista de documentos permite ubicarlo de nuevo."
                : CarpetaDescargasValida
                    ? $"El PDF descargado se guarda en: {_carpetaDescargas}"
                    : _carpetaDescargas == null
                        ? "Para agregar por URL, elegí antes una carpeta de descargas en Configuración (engranaje → Biblioteca)."
                        : $"La carpeta de descargas configurada no existe: {_carpetaDescargas}. Elegí otra en Configuración.";
            ActualizarBotonAgregar();
        }

        private void BtnExaminarArchivo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Documentos PDF (*.pdf)|*.pdf" };
            if (dlg.ShowDialog() == true)
            {
                TxtArchivo.Text = dlg.FileName;
                ActualizarBotonAgregar();
            }
        }

        private void Campo_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ActualizarBotonAgregar();

        private void ActualizarBotonAgregar()
        {
            if (BtnAgregar == null) return;
            bool tieneTitulo = !string.IsNullOrWhiteSpace(TxtTitulo.Text);
            bool tieneOrigen = RbOrigenArchivo.IsChecked == true
                ? !string.IsNullOrWhiteSpace(TxtArchivo.Text)
                : !string.IsNullOrWhiteSpace(TxtUrl.Text) && CarpetaDescargasValida;
            BtnAgregar.IsEnabled = tieneTitulo && tieneOrigen;
        }

        private async void BtnAgregar_Click(object sender, RoutedEventArgs e)
        {
            string titulo = TxtTitulo.Text.Trim();
            string autor = TxtAutor.Text.Trim();
            string anio = TxtAnio.Text.Trim();
            string fuente = TxtFuente.Text.Trim();
            bool esArchivo = RbOrigenArchivo.IsChecked == true;
            string archivoPath = TxtArchivo.Text.Trim();
            string url = TxtUrl.Text.Trim();

            BtnAgregar.IsEnabled = false;
            _log($"Agregando material: \"{titulo}\"...", "SPINNER", false, false);

            try
            {
                using var content = new MultipartFormDataContent
                {
                    { new StringContent(titulo), "titulo" },
                    { new StringContent(autor), "autor" },
                    { new StringContent(anio), "anio" },
                    { new StringContent(fuente), "fuente_editorial" },
                };

                if (esArchivo)
                {
                    content.Add(new StringContent(archivoPath), "ruta_archivo");
                }
                else
                {
                    content.Add(new StringContent(url), "origen_url");
                    content.Add(new StringContent(_carpetaDescargas ?? ""), "carpeta_descargas");
                }

                var resp = await _http.PostAsync($"{MainWindow.BackendBaseUrl}/ingestar", content);
                string body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    var data = JsonSerializer.Deserialize<IngestaResponseDto>(body, JsonOpts);
                    string advertencias = data?.advertencias != null && data.advertencias.Count > 0
                        ? $" Advertencias: {string.Join("; ", data.advertencias)}"
                        : "";
                    _log($"Material agregado: {data?.documento_id} — \"{data?.titulo}\" " +
                         $"({data?.n_paginas} páginas, {data?.n_chunks} fragmentos). PDF: {data?.ruta_archivo}{advertencias}", "OK", true, false);

                    await _refrescarDocumentos();

                    // Limpieza selectiva (Requisito 21): mantiene Autor/Año/Fuente
                    // para cargar varios artículos de la misma fuente seguidos.
                    TxtTitulo.Clear();
                    TxtArchivo.Clear();
                    TxtUrl.Clear();
                    TxtTitulo.Focus();
                }
                else if (resp.StatusCode == HttpStatusCode.Conflict)
                {
                    var wrapper = JsonSerializer.Deserialize<ErrorDetalleDuplicado>(body, JsonOpts);
                    string existente = wrapper?.detail?.documento_id_existente ?? "?";
                    string mensaje = wrapper?.detail?.mensaje ?? "Documento duplicado.";
                    _log($"{mensaje} (ya incorporado como {existente})", "ERROR", true, false);
                }
                else
                {
                    string mensaje = TryExtraerDetalle(body) ?? $"El backend respondió {(int)resp.StatusCode}.";
                    _log(mensaje, "ERROR", true, false);
                }
            }
            catch (Exception ex)
            {
                _log($"No se pudo agregar el material: {ex.Message}", "ERROR", true, false);
            }
            finally
            {
                ActualizarBotonAgregar();
            }
        }

        private static string? TryExtraerDetalle(string body)
        {
            try
            {
                var wrapper = JsonSerializer.Deserialize<ErrorDetalleSimple>(body, JsonOpts);
                return wrapper?.detail;
            }
            catch
            {
                return null;
            }
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
