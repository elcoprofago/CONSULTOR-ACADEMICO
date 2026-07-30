using System.Windows;

namespace ConsultorAcademicoGui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Licencia Community de QuestPDF (gratuita): uso propio, no
            // comercial, de esta herramienta de consulta jurídica.
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }
    }
}
