// CONSULTOR-ACADEMICO.exe de la carpeta portable: abre app\CONSULTOR-ACADEMICO-GUI.exe.
// Es .NET Framework 4 (viene con Windows 10 y 11) para no depender de nada;
// build_portable.py lo compila con el csc.exe de Windows.
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

static class Lanzador
{
    [STAThread]
    static int Main()
    {
        string app = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
        string exe = Path.Combine(app, "CONSULTOR-ACADEMICO-GUI.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show("No se encontró " + exe + ".\n\nLa carpeta está incompleta: volvé a armarla con build_portable.py.",
                "Consultor Académico", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = app, UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo abrir " + exe + ":\n\n" + ex.Message,
                "Consultor Académico", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
