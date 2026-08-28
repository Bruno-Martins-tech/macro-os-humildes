using System;
using System.Windows.Forms;

namespace MacroSupremes
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Aplica uma atualizacao pendente ANTES de abrir a UI (fail-open: se falhar, abre normal).
            try { AutoUpdater.AplicarUpdateInProcess(); }
            catch (Exception ex) { UpdLog.W("Main boot: " + ex.Message); }

            // Sinal de vida anonimo (telemetria de uso). Fire-and-forget, nao bloqueia a abertura.
            _ = Backend.EnviarHeartbeatAsync(AutoUpdater.VersaoAtual);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
