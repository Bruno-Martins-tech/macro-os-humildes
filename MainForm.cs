using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacroSupremes
{
    // ======================================================================
    // MODELO DE DADOS
    // ======================================================================

    public class MacroEvent
    {
        [JsonPropertyName("t")]
        public double T { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("button")]
        public string Button { get; set; } = "";

        [JsonPropertyName("key")]
        public int Key { get; set; }

        [JsonPropertyName("down")]
        public bool Down { get; set; }

        [JsonPropertyName("wheel")]
        public int Wheel { get; set; }
    }

    public class Macro
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Novo macro";

        [JsonPropertyName("hotkey")]
        public string Hotkey { get; set; } = "";

        [JsonPropertyName("repeticoes")]
        public int Repeticoes { get; set; } = 0;

        [JsonPropertyName("intervaloMs")]
        public int IntervaloMs { get; set; } = 1000;

        [JsonPropertyName("atrasoInicialMs")]
        public int AtrasoInicialMs { get; set; } = 0;

        [JsonPropertyName("eventos")]
        public List<MacroEvent> Eventos { get; set; } = new();

        public override string ToString()
        {
            string hotkey = string.IsNullOrEmpty(Hotkey) ? "" : $" [{Hotkey}]";
            return $"{Name}{hotkey}";
        }
    }

    public class ConfiguracoesApp
    {
        [JsonPropertyName("hotkeyGravar")]
        public string HotkeyGravar { get; set; } = "F9";

        [JsonPropertyName("hotkeyPanico")]
        public string HotkeyPanico { get; set; } = "Ctrl+F12";

        [JsonPropertyName("velocidade")]
        public double Velocidade { get; set; } = 1.0;
    }

    public class Biblioteca
    {
        [JsonPropertyName("macros")]
        public List<Macro> Macros { get; set; } = new();

        [JsonPropertyName("config")]
        public ConfiguracoesApp Config { get; set; } = new();
    }

    // ======================================================================
    // WIN32 P/INVOKE
    // ======================================================================

    // ======================================================================
    // AUTO-UPDATER via GitHub Releases
    // ======================================================================

    static class AutoUpdater
    {
        private const string GITHUB_USER = "Bruno-Martins-tech";
        private const string GITHUB_REPO = "macro-os-humildes";
        private const string CURRENT_VERSION = "1.8.0";
        private static readonly string API_URL = $"https://api.github.com/repos/{GITHUB_USER}/{GITHUB_REPO}/releases/latest";

        public static async Task<(bool temUpdate, string versaoNova, string downloadUrl)?> ChecarAtualizacao()
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "MacroSupremes-Updater");
                http.Timeout = TimeSpan.FromSeconds(8);

                var json = await http.GetStringAsync(API_URL);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                string versaoRemota = tagName.TrimStart('v', 'V');

                // Comparar versoes
                if (!Version.TryParse(versaoRemota, out var vRemota) ||
                    !Version.TryParse(CURRENT_VERSION, out var vLocal))
                    return null;

                if (vRemota <= vLocal)
                    return (false, "", "");

                // Encontrar o .exe no release
                string downloadUrl = "";
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string nome = asset.GetProperty("name").GetString() ?? "";
                        if (nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                return (true, versaoRemota, downloadUrl);
            }
            catch
            {
                return null; // sem internet ou repo nao existe ainda — ignorar silenciosamente
            }
        }

        public static async Task<bool> BaixarEAtualizar(string downloadUrl, Action<int> onProgress)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "MacroSupremes-Updater");

                using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                string exeAtual = Application.ExecutablePath;
                string exeNovo = exeAtual + ".update";
                string exeBackup = exeAtual + ".bak";

                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var file = File.Create(exeNovo))
                {
                    var buffer = new byte[81920];
                    long baixado = 0;
                    int lido;
                    while ((lido = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await file.WriteAsync(buffer, 0, lido);
                        baixado += lido;
                        if (totalBytes > 0)
                            onProgress((int)(baixado * 100 / totalBytes));
                    }
                }

                // Renomear: atual → .bak, novo → atual
                if (File.Exists(exeBackup)) File.Delete(exeBackup);
                File.Move(exeAtual, exeBackup);
                File.Move(exeNovo, exeAtual);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void ReiniciarApp()
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string bat = Path.Combine(Path.GetTempPath(), "macro_restart.cmd");

            // Bat que espera o app fechar e reabre (herda elevacao do processo pai)
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"start \"\" \"{exe}\"\r\n" +
                "del \"%~f0\"\r\n");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });

            Environment.Exit(0);
        }

        public static string VersaoAtual => CURRENT_VERSION;
    }

    // Player de musica via MCI (Windows Media)
    static class MciPlayer
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, System.Text.StringBuilder? buffer, int bufferSize, IntPtr callback);

        private static bool aberto;

        public static void Abrir(string caminho)
        {
            Fechar();
            mciSendString($"open \"{caminho}\" type mpegvideo alias wydmusic", null, 0, IntPtr.Zero);
            aberto = true;
        }

        public static void Tocar(bool loop = true)
        {
            if (!aberto) return;
            mciSendString("play wydmusic" + (loop ? " repeat" : ""), null, 0, IntPtr.Zero);
        }

        public static void SetVolume(int vol) // 0-1000
        {
            if (!aberto) return;
            mciSendString($"setaudio wydmusic volume to {vol}", null, 0, IntPtr.Zero);
        }

        public static void Pausar()
        {
            if (!aberto) return;
            mciSendString("pause wydmusic", null, 0, IntPtr.Zero);
        }

        public static void Continuar()
        {
            if (!aberto) return;
            mciSendString("resume wydmusic", null, 0, IntPtr.Zero);
        }

        public static void Fechar()
        {
            if (!aberto) return;
            mciSendString("close wydmusic", null, 0, IntPtr.Zero);
            aberto = false;
        }
    }

    // ======================================================================
    // PROXY HACK — Login em server full (proxy 0.0.0.4:80)
    // ======================================================================

    static class ProxyHack
    {
        private const string REG_PATH = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public static bool IsAtivo()
        {
            using var key = Registry.CurrentUser.OpenSubKey(REG_PATH);
            if (key == null) return false;
            int enable = (int)(key.GetValue("ProxyEnable", 0) ?? 0);
            string server = (string)(key.GetValue("ProxyServer", "") ?? "");
            return enable == 1 && server == "0.0.0.4:80";
        }

        public static void Ativar()
        {
            using var key = Registry.CurrentUser.OpenSubKey(REG_PATH, writable: true);
            if (key == null) return;
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", "0.0.0.4:80", RegistryValueKind.String);
            NotificarSistema();
        }

        public static void Desativar()
        {
            using var key = Registry.CurrentUser.OpenSubKey(REG_PATH, writable: true);
            if (key == null) return;
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            NotificarSistema();
        }

        private static void NotificarSistema()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }

    static class Win32
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;

        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int VK_ESCAPE = 0x1B;

        public const int INPUT_MOUSE = 0;
        public const int INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;

        public const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public int mouseData;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public int type; public INPUTUNION u; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx; public int dy; public int mouseData;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan;
            public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }
    }

    // ======================================================================
    // CONTROLES CUSTOMIZADOS — visual moderno
    // ======================================================================

    // Painel com cantos arredondados e fundo semi-transparente (card)
    public class CardPanel : Panel
    {
        public int Radius { get; set; } = 12;
        public Color CardColor { get; set; } = Color.FromArgb(38, 40, 48);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(ClientRectangle, Radius);
            using var brush = new SolidBrush(CardColor);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // nao pintar background padrao
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Botao moderno com hover, cantos arredondados
    public class ModernButton : Control
    {
        public Color BaseColor { get; set; } = Color.FromArgb(55, 58, 68);
        public Color HoverColor { get; set; } = Color.FromArgb(70, 74, 86);
        public Color PressColor { get; set; } = Color.FromArgb(45, 48, 56);
        public Color AccentColor { get; set; } = Color.Transparent;
        public int Radius { get; set; } = 8;

        private bool hovering;
        private bool pressing;

        public ModernButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9, FontStyle.Bold);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            Size = new Size(120, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg = pressing ? PressColor : hovering ? HoverColor : BaseColor;

            using var path = RoundedRect(rect, Radius);
            using var brush = new SolidBrush(bg);
            g.FillPath(brush, path);

            // Borda de acento (se tiver)
            if (AccentColor != Color.Transparent)
            {
                using var pen = new Pen(AccentColor, 1.5f);
                g.DrawPath(pen, path);
            }

            // Texto centralizado
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(80, 80, 80));
            g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), sf);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressing = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressing = false; Invalidate(); base.OnMouseUp(e); }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ======================================================================
    // FORMULARIO PRINCIPAL
    // ======================================================================

    public class MainForm : Form
    {
        private const string DISCORD_URL = "";

        // Cores do tema
        private static readonly Color BG_DARK = Color.FromArgb(18, 18, 24);
        private static readonly Color BG_CARD = Color.FromArgb(28, 30, 38);
        private static readonly Color BG_INPUT = Color.FromArgb(38, 40, 50);
        private static readonly Color ACCENT_GREEN = Color.FromArgb(76, 217, 100);
        private static readonly Color ACCENT_RED = Color.FromArgb(255, 69, 58);
        private static readonly Color ACCENT_BLUE = Color.FromArgb(88, 101, 242);
        private static readonly Color ACCENT_YELLOW = Color.FromArgb(255, 214, 10);
        private static readonly Color TEXT_PRIMARY = Color.FromArgb(240, 240, 245);
        private static readonly Color TEXT_SECONDARY = Color.FromArgb(142, 142, 147);
        private static readonly Color TEXT_DIM = Color.FromArgb(90, 90, 100);

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroSupremes");
        private static readonly string MacrosPath = Path.Combine(AppDataDir, "macros.json");

        // Dados
        private Biblioteca biblioteca = new();
        private Macro? macroSelecionado;

        // Gravacao
        private bool gravando;
        private Stopwatch? gravacaoStopwatch;
        private List<MacroEvent> eventosGravados = new();
        private double ultimoMoveT;

        // Reproducao
        private volatile bool reproduzindo;
        private int voltaAtual;
        private Macro? macroReproduzindo;

        // Hooks
        private Win32.HookProc? mouseHookProc;
        private Win32.HookProc? keyboardHookProc;
        private IntPtr mouseHookId = IntPtr.Zero;
        private IntPtr keyboardHookId = IntPtr.Zero;

        // Hotkeys
        private Dictionary<int, int> hotkeysRegistrados = new();
        private const int HOTKEY_PANICO_ID = 9999;
        private const int HOTKEY_GRAVAR_ID = 9998;

        // Controles
        private ListBox lstMacros = null!;
        private ComboBox cmbHotkey = null!;
        private NumericUpDown nudRepeticoes = null!;
        private NumericUpDown nudIntervalo = null!;
        private NumericUpDown nudAtraso = null!;
        private Label lblAcoes = null!;
        private Label lblStatus = null!;
        private Panel pnlStatusBar = null!;
        private ModernButton btnGravar = null!;
        private ModernButton btnPararGravacao = null!;
        private ModernButton btnTestar = null!;
        private ModernButton btnPararReproducao = null!;
        private Label lblEstadoGravacao = null!;

        // Velocidade
        private TrackBar trkVelocidade = null!;
        private Label lblVelocidadeValor = null!;

        // Abas
        private ModernButton btnTabMacros = null!;
        private ModernButton btnTabTutorial = null!;
        private ModernButton btnTabConfig = null!;
        private Panel pnlMacros = null!;
        private Panel pnlTutorial = null!;
        private Panel pnlConfig = null!;

        private bool carregandoCampos;
        private bool musicaTocando;
        private bool musicaMutada;

        // Caminho da musica do WYD (login.mp3)
        private static readonly string WYD_MUSIC_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wyd_launcher", "WYD Global", "music", "login.mp3");

        // Runas nordicas para decoracao (referencia ao WYD)
        private const string RUNAS = "\u16A0\u16A2\u16A6\u16A8\u16B1\u16B7\u16C1\u16C7\u16D2\u16DE";

        // Brasao da guild (carregado do brasao.jpg ao lado do exe)
        private Image? brasaoImg;

        public MainForm()
        {
            Text = "MACRO \u2022 SUPREMES  \u2014  With Your Destiny";
            ClientSize = new Size(620, 720);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG_DARK;
            ForeColor = TEXT_PRIMARY;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9);

            // Carregar brasao da guild
            string brasaoPath = Path.Combine(AppContext.BaseDirectory, "brasao.jpg");
            if (File.Exists(brasaoPath))
                brasaoImg = Image.FromFile(brasaoPath);

            CriarUI();
            CarregarBiblioteca();
            AtualizarListaMacros();
            MostrarAba("macros");
            IniciarMusica();
            ChecarAtualizacaoAsync();
        }

        private async void ChecarAtualizacaoAsync()
        {
            var result = await AutoUpdater.ChecarAtualizacao();
            if (result == null || !result.Value.temUpdate) return;

            var (_, versaoNova, downloadUrl) = result.Value;
            if (string.IsNullOrEmpty(downloadUrl)) return;

            var resp = MessageBox.Show(
                $"Nova versao disponivel: v{versaoNova}\n" +
                $"Versao atual: v{AutoUpdater.VersaoAtual}\n\n" +
                "Deseja atualizar agora?",
                "Atualizacao disponivel",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (resp != DialogResult.Yes) return;

            AtualizarStatus("Baixando atualizacao...", ACCENT_YELLOW);

            bool ok = await AutoUpdater.BaixarEAtualizar(downloadUrl, pct =>
            {
                AtualizarStatus($"Baixando atualizacao... {pct}%", ACCENT_YELLOW);
            });

            if (ok)
            {
                MessageBox.Show(
                    "Atualizacao concluida!\nO app vai reiniciar.",
                    "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                AutoUpdater.ReiniciarApp();
            }
            else
            {
                AtualizarStatus("Erro ao baixar atualizacao", ACCENT_RED);
            }
        }

        // ==================================================================
        // UI
        // ==================================================================

        private void CriarUI()
        {
            // --- HEADER com gradiente e runas nordicas ---
            var pnlHeader = new Panel { Dock = DockStyle.Top, Height = 100 };
            pnlHeader.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Gradiente escuro com tom azulado (atmosfera WYD)
                using var gradBrush = new LinearGradientBrush(
                    new Point(0, 0), new Point(pnlHeader.Width, pnlHeader.Height),
                    Color.FromArgb(12, 14, 28), Color.FromArgb(24, 18, 36));
                g.FillRectangle(gradBrush, pnlHeader.ClientRectangle);

                // Runas nordicas decorativas no fundo (bem sutis)
                using var fontRunas = new Font("Segoe UI Symbol", 22);
                using var brushRunas = new SolidBrush(Color.FromArgb(18, 180, 180, 220));
                for (int i = 0; i < RUNAS.Length; i++)
                    g.DrawString(RUNAS[i].ToString(), fontRunas, brushRunas, 8 + i * 60, 60);

                // Linha de acento dourada embaixo (referencia ao ouro WYD)
                using var goldPen = new Pen(Color.FromArgb(80, 212, 175, 55), 2);
                g.DrawLine(goldPen, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);

                // Brasao da guild
                int bx = 12, by = 6, bs = 82;
                if (brasaoImg != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(brasaoImg, bx, by, bs, bs);
                }

                int textX = bx + bs + 10;

                // Titulo com estilo WYD
                using var fontTitulo = new Font("Segoe UI", 17, FontStyle.Bold);
                using var brushGoldShadow = new SolidBrush(Color.FromArgb(40, 212, 175, 55));
                g.DrawString("MACRO \u2022 SUPREMES", fontTitulo, brushGoldShadow, textX + 1, by + 5);
                using var brushTitle = new SolidBrush(TEXT_PRIMARY);
                g.DrawString("MACRO \u2022 SUPREMES", fontTitulo, brushTitle, textX, by + 4);

                // Subtitulo com referencia WYD
                using var fontSub = new Font("Segoe UI", 8.5f);
                using var brushSub = new SolidBrush(Color.FromArgb(212, 175, 55));
                g.DrawString("With Your Destiny \u2022 Guilda Supremes \u2022 Server 3", fontSub, brushSub, textX + 2, by + 30);

                // Citacao nordica
                using var fontCit = new Font("Segoe UI", 7.5f, FontStyle.Italic);
                using var brushCit = new SolidBrush(Color.FromArgb(80, 180, 180, 200));
                g.DrawString("\"Os deuses favorecem os Supremes\"", fontCit,
                    brushCit, textX + 2, by + 48);

                // Versao
                using var fontVer = new Font("Segoe UI", 7);
                using var brushVer = new SolidBrush(TEXT_DIM);
                g.DrawString($"v{AutoUpdater.VersaoAtual}", fontVer, brushVer, pnlHeader.Width - 50, 8);
            };
            Controls.Add(pnlHeader);

            // --- ABAS ---
            var pnlTabs = new Panel { Location = new Point(0, 100), Size = new Size(620, 42), BackColor = Color.FromArgb(22, 24, 30) };
            Controls.Add(pnlTabs);

            btnTabMacros = new ModernButton
            {
                Text = "\u2694 MACROS",
                Location = new Point(16, 5),
                Size = new Size(110, 32),
                BaseColor = ACCENT_GREEN,
                HoverColor = Color.FromArgb(90, 230, 115),
                ForeColor = Color.FromArgb(10, 10, 10),
                Radius = 6
            };
            btnTabMacros.Click += (s, e) => MostrarAba("macros");
            pnlTabs.Controls.Add(btnTabMacros);

            btnTabTutorial = new ModernButton
            {
                Text = "\u2139 COMO USAR",
                Location = new Point(132, 5),
                Size = new Size(120, 32),
                BaseColor = Color.FromArgb(45, 47, 55),
                HoverColor = Color.FromArgb(60, 62, 72),
                Radius = 6
            };
            btnTabTutorial.Click += (s, e) => MostrarAba("tutorial");
            pnlTabs.Controls.Add(btnTabTutorial);

            btnTabConfig = new ModernButton
            {
                Text = "\u2699 CONFIG",
                Location = new Point(258, 5),
                Size = new Size(110, 32),
                BaseColor = Color.FromArgb(45, 47, 55),
                HoverColor = Color.FromArgb(60, 62, 72),
                Radius = 6
            };
            btnTabConfig.Click += (s, e) => MostrarAba("config");
            pnlTabs.Controls.Add(btnTabConfig);

            // Botao forcar update
            var btnUpdate = new ModernButton
            {
                Text = "\u2B06 Update",
                Location = new Point(412, 5),
                Size = new Size(90, 32),
                BaseColor = Color.FromArgb(45, 80, 45),
                HoverColor = Color.FromArgb(55, 100, 55),
                Radius = 6,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            btnUpdate.Click += async (s, e) =>
            {
                AtualizarStatus("Buscando atualizacao...", ACCENT_YELLOW);
                var result = await AutoUpdater.ChecarAtualizacao();
                if (result == null)
                {
                    AtualizarStatus("Nao foi possivel checar (sem internet?)", ACCENT_RED);
                    return;
                }
                if (!result.Value.temUpdate)
                {
                    AtualizarStatus($"Voce ja esta na versao mais recente (v{AutoUpdater.VersaoAtual})", ACCENT_GREEN);
                    return;
                }
                var (_, versaoNova, downloadUrl) = result.Value;
                if (string.IsNullOrEmpty(downloadUrl)) return;

                var resp = MessageBox.Show(
                    $"Nova versao disponivel: v{versaoNova}\nVersao atual: v{AutoUpdater.VersaoAtual}\n\nAtualizar agora?",
                    "Atualizacao", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (resp != DialogResult.Yes) return;

                AtualizarStatus("Baixando atualizacao...", ACCENT_YELLOW);
                bool ok = await AutoUpdater.BaixarEAtualizar(downloadUrl, pct =>
                    AtualizarStatus($"Baixando... {pct}%", ACCENT_YELLOW));

                if (ok)
                {
                    MessageBox.Show("Atualizacao concluida!\nO app vai reiniciar.",
                        "Sucesso", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    AutoUpdater.ReiniciarApp();
                }
                else
                    AtualizarStatus("Erro ao baixar atualizacao", ACCENT_RED);
            };
            pnlTabs.Controls.Add(btnUpdate);

            // Botao de som (musica do WYD)
            var btnSom = new ModernButton
            {
                Text = "\u266B  Som",
                Location = new Point(512, 5),
                Size = new Size(80, 32),
                BaseColor = Color.FromArgb(50, 42, 62),
                HoverColor = Color.FromArgb(65, 55, 80),
                Radius = 6,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnSom.Click += (s, e) =>
            {
                musicaMutada = !musicaMutada;
                if (musicaMutada)
                {
                    MciPlayer.Pausar();
                    btnSom.Text = "\u266B  Mudo";
                    btnSom.BaseColor = Color.FromArgb(60, 30, 30);
                    btnSom.HoverColor = Color.FromArgb(80, 40, 40);
                }
                else
                {
                    MciPlayer.Continuar();
                    btnSom.Text = "\u266B  Som";
                    btnSom.BaseColor = Color.FromArgb(50, 42, 62);
                    btnSom.HoverColor = Color.FromArgb(65, 55, 80);
                }
                btnSom.Invalidate();
            };
            pnlTabs.Controls.Add(btnSom);

            // --- PAGINA MACROS ---
            pnlMacros = new Panel { Location = new Point(0, 142), Size = new Size(620, 468), BackColor = BG_DARK };
            Controls.Add(pnlMacros);
            CriarPaginaMacros();

            // --- PAGINA TUTORIAL ---
            pnlTutorial = new Panel { Location = new Point(0, 142), Size = new Size(620, 468), BackColor = BG_DARK, Visible = false };
            Controls.Add(pnlTutorial);
            CriarPaginaTutorial();

            // --- PAGINA CONFIG ---
            pnlConfig = new Panel { Location = new Point(0, 142), Size = new Size(620, 468), BackColor = BG_DARK, Visible = false };
            Controls.Add(pnlConfig);
            CriarPaginaConfig();

            // --- STATUS BAR ---
            pnlStatusBar = new Panel { Location = new Point(0, 610), Size = new Size(620, 40), BackColor = Color.FromArgb(22, 24, 30) };
            pnlStatusBar.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(40, ACCENT_GREEN), 1);
                e.Graphics.DrawLine(pen, 0, 0, pnlStatusBar.Width, 0);
            };
            Controls.Add(pnlStatusBar);

            // Indicador de status (bolinha colorida + texto)
            var pnlDot = new Panel { Location = new Point(16, 14), Size = new Size(10, 10) };
            pnlDot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color dotColor = reproduzindo ? ACCENT_RED : gravando ? ACCENT_YELLOW : ACCENT_GREEN;
                using var brush = new SolidBrush(dotColor);
                e.Graphics.FillEllipse(brush, 0, 0, 9, 9);
            };
            pnlStatusBar.Controls.Add(pnlDot);

            lblStatus = new Label
            {
                Text = "Pronto",
                Location = new Point(32, 10),
                Size = new Size(400, 20),
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Tag = pnlDot // guardar referencia pra repintar o dot
            };
            pnlStatusBar.Controls.Add(lblStatus);

            // --- RODAPE ---
            var pnlRodape = new Panel { Location = new Point(0, 650), Size = new Size(620, 70), BackColor = Color.FromArgb(16, 16, 22) };
            Controls.Add(pnlRodape);

            // Botoes de acesso rapido
            var btnDiscord = new ModernButton
            {
                Text = "Discord",
                Location = new Point(16, 8),
                Size = new Size(90, 28),
                BaseColor = ACCENT_BLUE,
                HoverColor = Color.FromArgb(105, 118, 255),
                Radius = 6,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            btnDiscord.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(DISCORD_URL))
                    MessageBox.Show("Link do Discord ainda nao configurado.\nPeca ao admin para atualizar.",
                        "Discord", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    Process.Start(new ProcessStartInfo(DISCORD_URL) { UseShellExecute = true });
            };
            pnlRodape.Controls.Add(btnDiscord);

            var btnDroplist = new ModernButton
            {
                Text = "Droplist",
                Location = new Point(112, 8),
                Size = new Size(90, 28),
                BaseColor = Color.FromArgb(140, 90, 20),
                HoverColor = Color.FromArgb(170, 110, 30),
                Radius = 6,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            btnDroplist.Click += (s, e) =>
                Process.Start(new ProcessStartInfo("https://droplist.raidhut.com/") { UseShellExecute = true });
            pnlRodape.Controls.Add(btnDroplist);

            var btnUpdatesWyd = new ModernButton
            {
                Text = "Updates WYD",
                Location = new Point(208, 8),
                Size = new Size(110, 28),
                BaseColor = Color.FromArgb(50, 100, 50),
                HoverColor = Color.FromArgb(60, 125, 60),
                Radius = 6,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            btnUpdatesWyd.Click += (s, e) =>
                Process.Start(new ProcessStartInfo("https://wydglobal.raidhut.com/pt-br/3578") { UseShellExecute = true });
            pnlRodape.Controls.Add(btnUpdatesWyd);

            var lblCreditos = new Label
            {
                Text = "Criado por MartinS- \u2022 Supremes \u2022 Server 3",
                Location = new Point(340, 8),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8)
            };
            pnlRodape.Controls.Add(lblCreditos);

            var lblAviso = new Label
            {
                Text = "Use conforme as regras do seu servidor.",
                Location = new Point(340, 26),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 7)
            };
            pnlRodape.Controls.Add(lblAviso);
        }

        // --- PAGINA MACROS ---
        private void CriarPaginaMacros()
        {
            // Card esquerdo — Lista de macros
            var cardLista = new CardPanel
            {
                Location = new Point(16, 10),
                Size = new Size(230, 370),
                CardColor = BG_CARD
            };
            pnlMacros.Controls.Add(cardLista);

            var lblTitLista = new Label
            {
                Text = "SEUS MACROS",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardLista.Controls.Add(lblTitLista);

            lstMacros = new ListBox
            {
                Location = new Point(12, 38),
                Size = new Size(206, 250),
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 32
            };
            lstMacros.DrawItem += LstMacros_DrawItem;
            lstMacros.SelectedIndexChanged += LstMacros_SelectedIndexChanged;
            cardLista.Controls.Add(lstMacros);

            // Botoes da lista
            var btnNovo = new ModernButton
            {
                Text = "+ Novo",
                Location = new Point(12, 296),
                Size = new Size(65, 28),
                BaseColor = Color.FromArgb(40, 120, 60),
                HoverColor = Color.FromArgb(50, 140, 70),
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Radius = 6
            };
            btnNovo.Click += BtnNovo_Click;
            cardLista.Controls.Add(btnNovo);

            var btnRenomear = new ModernButton
            {
                Text = "Renomear",
                Location = new Point(82, 296),
                Size = new Size(72, 28),
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Radius = 6
            };
            btnRenomear.Click += BtnRenomear_Click;
            cardLista.Controls.Add(btnRenomear);

            var btnExcluir = new ModernButton
            {
                Text = "Excluir",
                Location = new Point(159, 296),
                Size = new Size(60, 28),
                BaseColor = Color.FromArgb(120, 35, 35),
                HoverColor = Color.FromArgb(150, 45, 45),
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Radius = 6
            };
            btnExcluir.Click += BtnExcluir_Click;
            cardLista.Controls.Add(btnExcluir);

            // Dica
            var lblDica = new Label
            {
                Text = "Dica: use F5-F12 com o jogo aberto",
                Location = new Point(12, 332),
                Size = new Size(206, 20),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 7.5f),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter
            };
            cardLista.Controls.Add(lblDica);

            // Card direito — Configuracoes
            var cardConfig = new CardPanel
            {
                Location = new Point(260, 10),
                Size = new Size(344, 266),
                CardColor = BG_CARD
            };
            pnlMacros.Controls.Add(cardConfig);

            var lblTitConfig = new Label
            {
                Text = "CONFIGURACOES",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardConfig.Controls.Add(lblTitConfig);

            // Campos
            int lx = 16, rx = 190, y = 42, gap = 38;

            AddLabel(cardConfig, "Atalho", lx, y);
            cmbHotkey = new ComboBox
            {
                Location = new Point(rx, y - 3),
                Size = new Size(100, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9)
            };
            cmbHotkey.Items.AddRange(new object[] { "(nenhum)", "F5", "F6", "F7", "F8", "F10", "F11", "F12" });
            cmbHotkey.SelectedIndex = 0;
            cmbHotkey.SelectedIndexChanged += CampoAlterado;
            cardConfig.Controls.Add(cmbHotkey);

            y += gap;
            AddLabel(cardConfig, "Repeticoes (0 = infinito)", lx, y);
            nudRepeticoes = CriarNumeric(new Point(rx, y - 3), 0, 99999, 0);
            nudRepeticoes.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudRepeticoes);

            y += gap;
            AddLabel(cardConfig, "Intervalo entre voltas (ms)", lx, y);
            nudIntervalo = CriarNumeric(new Point(rx, y - 3), 0, 999999, 1000);
            nudIntervalo.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudIntervalo);

            y += gap;
            AddLabel(cardConfig, "Atraso inicial (ms)", lx, y);
            nudAtraso = CriarNumeric(new Point(rx, y - 3), 0, 999999, 0);
            nudAtraso.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudAtraso);

            y += gap;
            AddLabel(cardConfig, "Velocidade", lx, y);
            trkVelocidade = new TrackBar
            {
                Location = new Point(rx - 10, y - 6),
                Size = new Size(120, 30),
                Minimum = 1,
                Maximum = 10,
                Value = (int)(biblioteca.Config.Velocidade * 2),
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                BackColor = BG_CARD
            };
            lblVelocidadeValor = new Label
            {
                Text = $"{biblioteca.Config.Velocidade:0.0}x",
                Location = new Point(rx + 115, y),
                AutoSize = true,
                ForeColor = ACCENT_YELLOW,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            trkVelocidade.ValueChanged += (s, ev) =>
            {
                double vel = trkVelocidade.Value / 2.0;
                biblioteca.Config.Velocidade = vel;
                lblVelocidadeValor.Text = $"{vel:0.0}x";
                SalvarBiblioteca();
            };
            cardConfig.Controls.Add(trkVelocidade);
            cardConfig.Controls.Add(lblVelocidadeValor);

            y += gap + 4;
            lblAcoes = new Label
            {
                Text = "0 acoes gravadas",
                Location = new Point(lx, y),
                AutoSize = true,
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardConfig.Controls.Add(lblAcoes);

            // Card de acoes (gravar/testar)
            var cardAcoes = new CardPanel
            {
                Location = new Point(260, 286),
                Size = new Size(344, 172),
                CardColor = BG_CARD
            };
            pnlMacros.Controls.Add(cardAcoes);

            var lblTitAcoes = new Label
            {
                Text = "ACOES",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAcoes.Controls.Add(lblTitAcoes);

            btnGravar = new ModernButton
            {
                Text = $"\u25CF  Gravar ({biblioteca.Config.HotkeyGravar})",
                Location = new Point(16, 42),
                Size = new Size(145, 34),
                BaseColor = Color.FromArgb(180, 40, 40),
                HoverColor = Color.FromArgb(210, 50, 50),
                AccentColor = ACCENT_RED,
                Radius = 8
            };
            btnGravar.Click += BtnGravar_Click;
            cardAcoes.Controls.Add(btnGravar);

            btnPararGravacao = new ModernButton
            {
                Text = "\u25A0  Parar (ESC)",
                Location = new Point(170, 42),
                Size = new Size(155, 34),
                Enabled = false,
                Radius = 8
            };
            btnPararGravacao.Click += BtnPararGravacao_Click;
            cardAcoes.Controls.Add(btnPararGravacao);

            btnTestar = new ModernButton
            {
                Text = "\u25B6  Testar",
                Location = new Point(16, 84),
                Size = new Size(145, 34),
                BaseColor = Color.FromArgb(40, 130, 50),
                HoverColor = Color.FromArgb(50, 155, 60),
                AccentColor = ACCENT_GREEN,
                Radius = 8
            };
            btnTestar.Click += BtnTestar_Click;
            cardAcoes.Controls.Add(btnTestar);

            btnPararReproducao = new ModernButton
            {
                Text = $"\u25A0  Parar ({biblioteca.Config.HotkeyPanico})",
                Location = new Point(170, 84),
                Size = new Size(155, 34),
                Enabled = false,
                Radius = 8
            };
            btnPararReproducao.Click += BtnPararReproducao_Click;
            cardAcoes.Controls.Add(btnPararReproducao);

            // Label de estado da gravacao (feedback claro)
            lblEstadoGravacao = new Label
            {
                Text = "",
                Location = new Point(16, 122),
                Size = new Size(310, 22),
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            cardAcoes.Controls.Add(lblEstadoGravacao);

            // Botao Salvar + Limpar gravacao
            var btnSalvarMacro = new ModernButton
            {
                Text = "\u2714  Salvar",
                Location = new Point(16, 144),
                Size = new Size(110, 28),
                BaseColor = Color.FromArgb(30, 90, 150),
                HoverColor = Color.FromArgb(40, 110, 180),
                AccentColor = ACCENT_BLUE,
                Radius = 8
            };
            btnSalvarMacro.Click += (s, e) =>
            {
                SalvarBiblioteca();
                MostrarFeedbackGravacao("\u2714  Macro salvo com sucesso!", ACCENT_GREEN);
            };
            cardAcoes.Controls.Add(btnSalvarMacro);

            var btnLimpar = new ModernButton
            {
                Text = "\u2716  Limpar",
                Location = new Point(134, 144),
                Size = new Size(110, 28),
                BaseColor = Color.FromArgb(100, 40, 40),
                HoverColor = Color.FromArgb(130, 50, 50),
                Radius = 8
            };
            btnLimpar.Click += (s, e) =>
            {
                if (macroSelecionado == null) return;
                if (macroSelecionado.Eventos.Count == 0) return;
                if (MessageBox.Show($"Limpar a gravacao de \"{macroSelecionado.Name}\"?",
                    "Confirmar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                macroSelecionado.Eventos.Clear();
                SalvarBiblioteca();
                CarregarCamposDoMacro();
                MostrarFeedbackGravacao("Gravacao removida", ACCENT_YELLOW);
            };
            cardAcoes.Controls.Add(btnLimpar);

            // Card atalhos rapidos
            var cardAtalhos = new CardPanel
            {
                Location = new Point(16, 390),
                Size = new Size(588, 68),
                CardColor = Color.FromArgb(24, 26, 34)
            };
            pnlMacros.Controls.Add(cardAtalhos);

            var lblAtalhos = new Label
            {
                Text = "ATALHOS GLOBAIS",
                Location = new Point(16, 10),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 7, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblAtalhos);

            var lblAtalhosInfo = new Label
            {
                Text = $"F5-F8  macro   {biblioteca.Config.HotkeyGravar}  gravar   {biblioteca.Config.HotkeyPanico}  parar tudo",
                Location = new Point(16, 32),
                Size = new Size(500, 20),
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Consolas", 8.5f),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblAtalhosInfo);
        }

        // --- PAGINA TUTORIAL ---
        private void CriarPaginaTutorial()
        {
            var scroll = new Panel
            {
                Location = new Point(16, 10),
                Size = new Size(588, 450),
                AutoScroll = true,
                BackColor = BG_DARK
            };
            pnlTutorial.Controls.Add(scroll);

            int y = 0;

            y = AddTutorialStep(scroll, y, "1", "GRAVAR UM MACRO",
                "1. Selecione um macro na lista (ex: \"Auto Pergaminho da Agua\")\n" +
                $"2. Pressione  {biblioteca.Config.HotkeyGravar}  ou clique no botao  \u25CF Gravar\n" +
                "3. Espere a contagem regressiva de 3 segundos\n" +
                "4. Faca as acoes no jogo (cliques, teclas)\n" +
                "5. Pressione  ESC  para parar a gravacao",
                ACCENT_RED);

            y = AddTutorialStep(scroll, y, "2", "TESTAR O MACRO",
                "1. Selecione o macro gravado\n" +
                "2. Clique no botao  \u25B6 Testar\n" +
                "3. O macro vai repetir suas acoes automaticamente\n" +
                $"4. Para parar:  {biblioteca.Config.HotkeyPanico}  ou clique em Parar",
                ACCENT_GREEN);

            y = AddTutorialStep(scroll, y, "3", "USAR COM O WYD",
                "1. Abra o WYD e este app (os dois precisam rodar como admin)\n" +
                "2. Configure o atalho do macro (F5, F6, etc.)\n" +
                "3. Va para o jogo e pressione o atalho (ex: F5)\n" +
                "4. O macro roda em loop enquanto voce joga\n" +
                $"5.  {biblioteca.Config.HotkeyPanico}  para tudo de qualquer tela",
                ACCENT_BLUE);

            y = AddTutorialStep(scroll, y, "4", "AUTO CHAT (JA PRONTO)",
                "O macro \"Auto Chat\" ja vem configurado:\n" +
                "\u2022  Digite sua mensagem no chat do jogo e envie\n" +
                "\u2022  Pressione  F7  para ativar o auto-reenvio\n" +
                "\u2022  A cada 12s ele repete: Enter \u2192 Seta cima \u2192 Enter\n" +
                "\u2022  Perfeito para divulgar compra/venda no chat",
                ACCENT_YELLOW);

            y = AddTutorialStep(scroll, y, "5", "DICAS AVANCADAS",
                "\u2022  Repeticoes = 0  significa loop infinito\n" +
                "\u2022  Atraso inicial: util pra usar item quando fica disponivel (AFK)\n" +
                "\u2022  Intervalo entre voltas: pausa entre cada repeticao\n" +
                "\u2022  Cada pessoa precisa gravar seus proprios macros de inventario\n" +
                "   (as posicoes dos slots variam por resolucao de tela)",
                TEXT_SECONDARY);
        }

        private int AddTutorialStep(Panel parent, int y, string num, string titulo, string corpo, Color accent)
        {
            var card = new CardPanel
            {
                Location = new Point(0, y),
                Size = new Size(566, 0), // altura calculada
                CardColor = BG_CARD
            };

            // Numero do passo (circulo colorido)
            var pnlNum = new Panel { Location = new Point(16, 16), Size = new Size(32, 32), BackColor = Color.Transparent };
            pnlNum.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(accent);
                e.Graphics.FillEllipse(brush, 0, 0, 30, 30);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var font = new Font("Segoe UI", 12, FontStyle.Bold);
                using var brushNum = new SolidBrush(Color.FromArgb(10, 10, 10));
                e.Graphics.DrawString(num, font, brushNum, new RectangleF(0, 0, 30, 30), sf);
            };
            card.Controls.Add(pnlNum);

            // Titulo
            var lblTit = new Label
            {
                Text = titulo,
                Location = new Point(58, 18),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            card.Controls.Add(lblTit);

            // Corpo
            var lblCorpo = new Label
            {
                Text = corpo,
                Location = new Point(58, 48),
                Size = new Size(490, 0),
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = Color.Transparent,
                AutoSize = true
            };
            card.Controls.Add(lblCorpo);

            // Calcular altura do card
            int cardHeight = lblCorpo.Bottom + 18;
            card.Size = new Size(566, cardHeight);

            parent.Controls.Add(card);
            return y + cardHeight + 10;
        }

        // --- PAGINA CONFIGURACOES ---
        private ComboBox cmbConfigGravar = null!;
        private ComboBox cmbConfigPanico = null!;

        private void CriarPaginaConfig()
        {
            // Card — Atalhos globais
            var cardAtalhos = new CardPanel
            {
                Location = new Point(16, 10),
                Size = new Size(588, 200),
                CardColor = BG_CARD
            };
            pnlConfig.Controls.Add(cardAtalhos);

            var lblTitAtalhos = new Label
            {
                Text = "ATALHOS GLOBAIS",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblTitAtalhos);

            var lblDescAtalhos = new Label
            {
                Text = "Personalize as teclas de atalho do app. As mudancas sao salvas automaticamente.",
                Location = new Point(16, 36),
                Size = new Size(550, 18),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblDescAtalhos);

            int lx = 16, rx = 260, y = 66, gap = 42;

            // Hotkey gravar
            AddLabel(cardAtalhos, "Gravar / Parar gravacao", lx, y);
            cmbConfigGravar = new ComboBox
            {
                Location = new Point(rx, y - 3),
                Size = new Size(140, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9)
            };
            cmbConfigGravar.Items.AddRange(new object[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" });
            cmbConfigGravar.SelectedItem = biblioteca.Config.HotkeyGravar;
            cmbConfigGravar.SelectedIndexChanged += (s, ev) =>
            {
                biblioteca.Config.HotkeyGravar = cmbConfigGravar.SelectedItem?.ToString() ?? "F9";
                SalvarBiblioteca();
                RegistrarHotkeys();
            };
            cardAtalhos.Controls.Add(cmbConfigGravar);

            y += gap;

            // Hotkey panico
            AddLabel(cardAtalhos, "Panico (parar tudo)", lx, y);
            cmbConfigPanico = new ComboBox
            {
                Location = new Point(rx, y - 3),
                Size = new Size(140, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9)
            };
            cmbConfigPanico.Items.AddRange(new object[] {
                "Ctrl+F1", "Ctrl+F2", "Ctrl+F3", "Ctrl+F4", "Ctrl+F5", "Ctrl+F6",
                "Ctrl+F7", "Ctrl+F8", "Ctrl+F9", "Ctrl+F10", "Ctrl+F11", "Ctrl+F12"
            });
            cmbConfigPanico.SelectedItem = biblioteca.Config.HotkeyPanico;
            cmbConfigPanico.SelectedIndexChanged += (s, ev) =>
            {
                biblioteca.Config.HotkeyPanico = cmbConfigPanico.SelectedItem?.ToString() ?? "Ctrl+F12";
                SalvarBiblioteca();
                RegistrarHotkeys();
            };
            cardAtalhos.Controls.Add(cmbConfigPanico);

            y += gap;

            // Nota
            var lblNota = new Label
            {
                Text = "Os atalhos dos macros (F5, F6, etc.) sao configurados na aba Macros, no campo \"Atalho\" de cada macro.",
                Location = new Point(16, y),
                Size = new Size(550, 32),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8.5f),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblNota);

            // Card — Velocidade
            var cardVelocidade = new CardPanel
            {
                Location = new Point(16, 220),
                Size = new Size(588, 140),
                CardColor = BG_CARD
            };
            pnlConfig.Controls.Add(cardVelocidade);

            var lblTitVel = new Label
            {
                Text = "VELOCIDADE DE REPRODUCAO",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardVelocidade.Controls.Add(lblTitVel);

            var lblDescVel = new Label
            {
                Text = "Multiplica a velocidade do replay. 1.0x = velocidade original. 5.0x = 5 vezes mais rapido.",
                Location = new Point(16, 36),
                Size = new Size(550, 18),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            };
            cardVelocidade.Controls.Add(lblDescVel);

            var trkVelConfig = new TrackBar
            {
                Location = new Point(16, 66),
                Size = new Size(420, 30),
                Minimum = 1,
                Maximum = 10,
                Value = (int)(biblioteca.Config.Velocidade * 2),
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                BackColor = BG_CARD
            };
            var lblVelConfig = new Label
            {
                Text = $"{biblioteca.Config.Velocidade:0.0}x",
                Location = new Point(445, 68),
                AutoSize = true,
                ForeColor = ACCENT_YELLOW,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            trkVelConfig.ValueChanged += (s, ev) =>
            {
                double vel = trkVelConfig.Value / 2.0;
                biblioteca.Config.Velocidade = vel;
                lblVelConfig.Text = $"{vel:0.0}x";
                // Sincronizar com o slider da aba macros
                if (trkVelocidade.Value != trkVelConfig.Value) trkVelocidade.Value = trkVelConfig.Value;
                SalvarBiblioteca();
            };
            // Sincronizar slider da aba macros com este
            trkVelocidade.ValueChanged += (s, ev) =>
            {
                if (trkVelConfig.Value != trkVelocidade.Value) trkVelConfig.Value = trkVelocidade.Value;
            };
            cardVelocidade.Controls.Add(trkVelConfig);
            cardVelocidade.Controls.Add(lblVelConfig);

            var lblVelLabels = new Label
            {
                Text = "0.5x                    1.0x                    2.0x                    3.0x                    4.0x                    5.0x",
                Location = new Point(16, 100),
                Size = new Size(450, 16),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 7),
                BackColor = Color.Transparent
            };
            cardVelocidade.Controls.Add(lblVelLabels);

            // Card — Hack Login Server Full
            var cardProxy = new CardPanel
            {
                Location = new Point(16, 370),
                Size = new Size(588, 90),
                CardColor = BG_CARD
            };
            pnlConfig.Controls.Add(cardProxy);

            var lblTitProxy = new Label
            {
                Text = "LOGIN SERVER FULL",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardProxy.Controls.Add(lblTitProxy);

            var lblDescProxy = new Label
            {
                Text = "Ativa proxy gateway (0.0.0.4:80) para burlar fila de servidor lotado. Desative apos logar.",
                Location = new Point(16, 36),
                Size = new Size(400, 18),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            };
            cardProxy.Controls.Add(lblDescProxy);

            bool proxyAtivo = ProxyHack.IsAtivo();
            var btnProxy = new ModernButton
            {
                Text = proxyAtivo ? "\u2714  HACK ATIVO" : "\u26A1  Ativar Hack Login",
                Location = new Point(16, 58),
                Size = new Size(200, 28),
                BaseColor = proxyAtivo ? Color.FromArgb(40, 140, 40) : Color.FromArgb(140, 50, 20),
                HoverColor = proxyAtivo ? Color.FromArgb(50, 170, 50) : Color.FromArgb(170, 60, 25),
                AccentColor = proxyAtivo ? ACCENT_GREEN : ACCENT_RED,
                Radius = 6,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnProxy.Click += (s, ev) =>
            {
                bool ativo = ProxyHack.IsAtivo();
                if (ativo)
                {
                    ProxyHack.Desativar();
                    btnProxy.Text = "\u26A1  Ativar Hack Login";
                    btnProxy.BaseColor = Color.FromArgb(140, 50, 20);
                    btnProxy.HoverColor = Color.FromArgb(170, 60, 25);
                    btnProxy.AccentColor = ACCENT_RED;
                    AtualizarStatus("Proxy desativado. Conexao normal.", ACCENT_GREEN);
                }
                else
                {
                    ProxyHack.Ativar();
                    btnProxy.Text = "\u2714  HACK ATIVO";
                    btnProxy.BaseColor = Color.FromArgb(40, 140, 40);
                    btnProxy.HoverColor = Color.FromArgb(50, 170, 50);
                    btnProxy.AccentColor = ACCENT_GREEN;
                    AtualizarStatus("Proxy ativado! Logue no WYD e depois desative.", ACCENT_YELLOW);
                }
                btnProxy.Invalidate();
            };
            cardProxy.Controls.Add(btnProxy);
        }

        // Desenho customizado da ListBox
        private void LstMacros_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selected ? Color.FromArgb(50, ACCENT_GREEN.R, ACCENT_GREEN.G, ACCENT_GREEN.B) : BG_INPUT;
            Color fg = selected ? ACCENT_GREEN : TEXT_PRIMARY;

            using var bgBrush = new SolidBrush(bg);
            g.FillRectangle(bgBrush, e.Bounds);

            if (selected)
            {
                using var accentPen = new Pen(ACCENT_GREEN, 2);
                g.DrawLine(accentPen, e.Bounds.X, e.Bounds.Y, e.Bounds.X, e.Bounds.Bottom);
            }

            string text = lstMacros.Items[e.Index].ToString() ?? "";
            using var font = new Font("Segoe UI", 9.5f);
            using var textBrush = new SolidBrush(fg);
            g.DrawString(text, font, textBrush, e.Bounds.X + 10, e.Bounds.Y + 6);
        }

        // Helpers
        private void AddLabel(Control parent, string text, int x, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.Transparent
            });
        }

        private NumericUpDown CriarNumeric(Point loc, int min, int max, int val)
        {
            return new NumericUpDown
            {
                Location = loc,
                Size = new Size(110, 26),
                Minimum = min,
                Maximum = max,
                Value = val,
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10)
            };
        }

        private void IniciarMusica()
        {
            if (File.Exists(WYD_MUSIC_PATH))
            {
                MciPlayer.Abrir(WYD_MUSIC_PATH);
                MciPlayer.SetVolume(150); // volume baixinho (0-1000)
                MciPlayer.Tocar(loop: true);
                musicaTocando = true;
            }
        }

        private void MostrarAba(string aba)
        {
            pnlMacros.Visible = aba == "macros";
            pnlTutorial.Visible = aba == "tutorial";
            pnlConfig.Visible = aba == "config";

            foreach (var (btn, id) in new[] { (btnTabMacros, "macros"), (btnTabTutorial, "tutorial"), (btnTabConfig, "config") })
            {
                bool ativo = aba == id;
                btn.BaseColor = ativo ? ACCENT_GREEN : Color.FromArgb(45, 47, 55);
                btn.ForeColor = ativo ? Color.FromArgb(10, 10, 10) : TEXT_PRIMARY;
                btn.Invalidate();
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ==================================================================
        // PERSISTENCIA
        // ==================================================================

        private void CarregarBiblioteca()
        {
            // Migrar dados da pasta antiga (MacroOsHumildes → MacroSupremes)
            if (!File.Exists(MacrosPath))
            {
                string pastaAntiga = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroOsHumildes");
                string arquivoAntigo = Path.Combine(pastaAntiga, "macros.json");
                if (File.Exists(arquivoAntigo))
                {
                    try
                    {
                        Directory.CreateDirectory(AppDataDir);
                        File.Copy(arquivoAntigo, MacrosPath);
                    }
                    catch { }
                }
            }

            if (File.Exists(MacrosPath))
            {
                try
                {
                    string json = File.ReadAllText(MacrosPath);
                    biblioteca = JsonSerializer.Deserialize<Biblioteca>(json) ?? new Biblioteca();
                }
                catch { biblioteca = new Biblioteca(); }
            }

            if (biblioteca.Macros.Count == 0)
            {
                biblioteca.Macros = CriarMacrosPadrao();
                SalvarBiblioteca();
            }
        }

        private List<Macro> CriarMacrosPadrao()
        {
            return new List<Macro>
            {
                new Macro { Name = "Auto Pergaminho da Agua", Hotkey = "F5", IntervaloMs = 3000, Repeticoes = 0 },
                new Macro { Name = "Auto Up de Montaria", Hotkey = "F6", IntervaloMs = 3000, Repeticoes = 0 },
                new Macro
                {
                    Name = "Auto Chat (divulgacao)",
                    Hotkey = "F7", IntervaloMs = 12000, Repeticoes = 0,
                    Eventos = GerarEventosAutoChat()
                },
                new Macro { Name = "Auto-uso de item", Hotkey = "F8", IntervaloMs = 30000, Repeticoes = 0 }
            };
        }

        private List<MacroEvent> GerarEventosAutoChat()
        {
            var eventos = new List<MacroEvent>();
            double t = 0;
            void Tecla(int vk, ref double tempo)
            {
                eventos.Add(new MacroEvent { T = tempo, Type = "key", Key = vk, Down = true });
                tempo += 0.05;
                eventos.Add(new MacroEvent { T = tempo, Type = "key", Key = vk, Down = false });
                tempo += 0.2;
            }
            Tecla(0x0D, ref t);
            Tecla(0x26, ref t);
            Tecla(0x0D, ref t);
            return eventos;
        }

        private void SalvarBiblioteca()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                string json = JsonSerializer.Serialize(biblioteca, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(MacrosPath, json);
                AtualizarStatus("Biblioteca salva", ACCENT_GREEN);
            }
            catch (Exception ex)
            {
                AtualizarStatus($"Erro ao salvar: {ex.Message}", ACCENT_RED);
            }
        }

        // ==================================================================
        // UI — Atualizacao de lista e campos
        // ==================================================================

        private void AtualizarListaMacros()
        {
            int idx = lstMacros.SelectedIndex;
            lstMacros.Items.Clear();
            foreach (var m in biblioteca.Macros)
                lstMacros.Items.Add(m.ToString());

            if (idx >= 0 && idx < lstMacros.Items.Count)
                lstMacros.SelectedIndex = idx;
            else if (lstMacros.Items.Count > 0)
                lstMacros.SelectedIndex = 0;
        }

        private void LstMacros_SelectedIndexChanged(object? sender, EventArgs e)
        {
            int idx = lstMacros.SelectedIndex;
            if (idx < 0 || idx >= biblioteca.Macros.Count) { macroSelecionado = null; return; }
            macroSelecionado = biblioteca.Macros[idx];
            CarregarCamposDoMacro();
        }

        private void CarregarCamposDoMacro()
        {
            if (macroSelecionado == null) return;
            carregandoCampos = true;

            if (string.IsNullOrEmpty(macroSelecionado.Hotkey))
                cmbHotkey.SelectedIndex = 0;
            else
            {
                int i = cmbHotkey.Items.IndexOf(macroSelecionado.Hotkey);
                cmbHotkey.SelectedIndex = i >= 0 ? i : 0;
            }

            nudRepeticoes.Value = macroSelecionado.Repeticoes;
            nudIntervalo.Value = macroSelecionado.IntervaloMs;
            nudAtraso.Value = macroSelecionado.AtrasoInicialMs;
            lblAcoes.Text = $"{macroSelecionado.Eventos.Count} acoes gravadas";

            carregandoCampos = false;
        }

        private void CampoAlterado(object? sender, EventArgs e)
        {
            if (carregandoCampos || macroSelecionado == null) return;

            string hotkeyAnterior = macroSelecionado.Hotkey;
            macroSelecionado.Hotkey = cmbHotkey.SelectedIndex <= 0 ? "" : cmbHotkey.SelectedItem!.ToString()!;
            macroSelecionado.Repeticoes = (int)nudRepeticoes.Value;
            macroSelecionado.IntervaloMs = (int)nudIntervalo.Value;
            macroSelecionado.AtrasoInicialMs = (int)nudAtraso.Value;

            int idx = lstMacros.SelectedIndex;
            if (idx >= 0) lstMacros.Items[idx] = macroSelecionado.ToString();
            if (hotkeyAnterior != macroSelecionado.Hotkey) RegistrarHotkeys();
            SalvarBiblioteca();
        }

        // Feedback claro no card de acoes (aparece e some apos 4s)
        private void MostrarFeedbackGravacao(string msg, Color cor)
        {
            if (InvokeRequired) { BeginInvoke(() => MostrarFeedbackGravacao(msg, cor)); return; }
            lblEstadoGravacao.Text = msg;
            lblEstadoGravacao.ForeColor = cor;

            // Timer pra sumir apos 4 segundos
            var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            timer.Tick += (s, e) =>
            {
                lblEstadoGravacao.Text = "";
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }

        private void AtualizarStatus(string msg, Color? cor = null)
        {
            if (InvokeRequired) { BeginInvoke(() => AtualizarStatus(msg, cor)); return; }
            lblStatus.Text = msg;
            lblStatus.ForeColor = cor ?? ACCENT_GREEN;
            // Repintar o dot de status
            if (lblStatus.Tag is Panel dot) dot.Invalidate();
        }

        // ==================================================================
        // BOTOES — Novo / Renomear / Excluir
        // ==================================================================

        private void BtnNovo_Click(object? sender, EventArgs e)
        {
            string nome = PedirTexto("Novo macro", "Nome do macro:", "Novo macro");
            if (string.IsNullOrWhiteSpace(nome)) return;
            biblioteca.Macros.Add(new Macro { Name = nome });
            SalvarBiblioteca();
            AtualizarListaMacros();
            lstMacros.SelectedIndex = biblioteca.Macros.Count - 1;
        }

        private void BtnRenomear_Click(object? sender, EventArgs e)
        {
            if (macroSelecionado == null) return;
            string nome = PedirTexto("Renomear", "Novo nome:", macroSelecionado.Name);
            if (string.IsNullOrWhiteSpace(nome)) return;
            macroSelecionado.Name = nome;
            SalvarBiblioteca();
            AtualizarListaMacros();
        }

        private void BtnExcluir_Click(object? sender, EventArgs e)
        {
            if (macroSelecionado == null) return;
            if (MessageBox.Show($"Excluir \"{macroSelecionado.Name}\"?", "Confirmar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            biblioteca.Macros.Remove(macroSelecionado);
            macroSelecionado = null;
            SalvarBiblioteca();
            AtualizarListaMacros();
            RegistrarHotkeys();
        }

        private string PedirTexto(string titulo, string prompt, string valorInicial)
        {
            var form = new Form
            {
                Text = titulo,
                ClientSize = new Size(350, 120),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = BG_DARK, ForeColor = TEXT_PRIMARY
            };

            var lbl = new Label { Text = prompt, Location = new Point(16, 16), AutoSize = true, ForeColor = TEXT_SECONDARY };
            var txt = new TextBox
            {
                Text = valorInicial,
                Location = new Point(16, 42),
                Size = new Size(318, 28),
                BackColor = BG_INPUT,
                ForeColor = TEXT_PRIMARY,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10)
            };
            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(178, 80),
                Size = new Size(75, 30),
                BackColor = Color.FromArgb(40, 130, 50),
                ForeColor = TEXT_PRIMARY,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnOk.FlatAppearance.BorderSize = 0;
            var btnCancel = new Button
            {
                Text = "Cancelar",
                DialogResult = DialogResult.Cancel,
                Location = new Point(260, 80),
                Size = new Size(75, 30),
                BackColor = Color.FromArgb(55, 58, 68),
                ForeColor = TEXT_PRIMARY,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnCancel.FlatAppearance.BorderSize = 0;

            form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            form.AcceptButton = btnOk;
            form.CancelButton = btnCancel;

            return form.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : "";
        }

        // ==================================================================
        // GRAVACAO
        // ==================================================================

        private void BtnGravar_Click(object? sender, EventArgs e)
        {
            if (macroSelecionado == null) { AtualizarStatus("Selecione um macro primeiro", ACCENT_YELLOW); return; }
            if (gravando || reproduzindo) return;

            btnGravar.Enabled = false;
            btnPararGravacao.Enabled = true;

            Task.Run(() =>
            {
                for (int i = 3; i > 0; i--)
                {
                    AtualizarStatus($"Gravacao comeca em {i}...", ACCENT_YELLOW);
                    Thread.Sleep(1000);
                }
                BeginInvoke(IniciarGravacao);
            });
        }

        private void IniciarGravacao()
        {
            gravando = true;
            eventosGravados = new List<MacroEvent>();
            ultimoMoveT = -1;
            gravacaoStopwatch = Stopwatch.StartNew();
            AtualizarStatus("GRAVANDO...  (ESC para parar)", ACCENT_RED);

            IntPtr hMod = Win32.GetModuleHandle(null!);
            mouseHookProc = MouseHookCallback;
            mouseHookId = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, mouseHookProc, hMod, 0);
            keyboardHookProc = KeyboardHookCallback;
            keyboardHookId = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, keyboardHookProc, hMod, 0);
        }

        private void PararGravacao()
        {
            if (!gravando) return;
            gravando = false;

            if (mouseHookId != IntPtr.Zero) { Win32.UnhookWindowsHookEx(mouseHookId); mouseHookId = IntPtr.Zero; }
            if (keyboardHookId != IntPtr.Zero) { Win32.UnhookWindowsHookEx(keyboardHookId); keyboardHookId = IntPtr.Zero; }
            gravacaoStopwatch?.Stop();

            if (macroSelecionado != null)
            {
                macroSelecionado.Eventos = new List<MacroEvent>(eventosGravados);
                SalvarBiblioteca();
                CarregarCamposDoMacro();
            }

            int totalAcoes = eventosGravados.Count;
            BeginInvoke(() =>
            {
                btnGravar.Enabled = true;
                btnPararGravacao.Enabled = false;
                AtualizarStatus($"Gravacao finalizada  \u2022  {totalAcoes} acoes", ACCENT_GREEN);
                MostrarFeedbackGravacao(
                    $"\u2714  {totalAcoes} acoes gravadas e salvas em \"{macroSelecionado?.Name}\"",
                    ACCENT_GREEN);
            });
        }

        private void BtnPararGravacao_Click(object? sender, EventArgs e) => PararGravacao();

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && gravando && gravacaoStopwatch != null)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                double t = gravacaoStopwatch.Elapsed.TotalSeconds;

                switch (msg)
                {
                    case Win32.WM_MOUSEMOVE:
                        if (t - ultimoMoveT >= 0.02)
                        {
                            eventosGravados.Add(new MacroEvent { T = t, Type = "move", X = info.pt.x, Y = info.pt.y });
                            ultimoMoveT = t;
                        }
                        break;
                    case Win32.WM_LBUTTONDOWN:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "left", Down = true }); break;
                    case Win32.WM_LBUTTONUP:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "left", Down = false }); break;
                    case Win32.WM_RBUTTONDOWN:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "right", Down = true }); break;
                    case Win32.WM_RBUTTONUP:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "right", Down = false }); break;
                    case Win32.WM_MBUTTONDOWN:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "middle", Down = true }); break;
                    case Win32.WM_MBUTTONUP:
                        eventosGravados.Add(new MacroEvent { T = t, Type = "mouse", X = info.pt.x, Y = info.pt.y, Button = "middle", Down = false }); break;
                    case Win32.WM_MOUSEWHEEL:
                        int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                        eventosGravados.Add(new MacroEvent { T = t, Type = "scroll", X = info.pt.x, Y = info.pt.y, Wheel = delta }); break;
                }
            }
            return Win32.CallNextHookEx(mouseHookId, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && gravando && gravacaoStopwatch != null)
            {
                var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if (info.vkCode == Win32.VK_ESCAPE)
                {
                    BeginInvoke(PararGravacao);
                    return (IntPtr)1;
                }
                double t = gravacaoStopwatch.Elapsed.TotalSeconds;
                int msg = (int)wParam;
                bool down = (msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN);
                eventosGravados.Add(new MacroEvent { T = t, Type = "key", Key = (int)info.vkCode, Down = down });
            }
            return Win32.CallNextHookEx(keyboardHookId, nCode, wParam, lParam);
        }

        // ==================================================================
        // REPRODUCAO
        // ==================================================================

        private void BtnTestar_Click(object? sender, EventArgs e)
        {
            if (macroSelecionado == null) { AtualizarStatus("Selecione um macro primeiro", ACCENT_YELLOW); return; }
            IniciarReproducao(macroSelecionado);
        }

        private void BtnPararReproducao_Click(object? sender, EventArgs e) => PararReproducao();

        private void IniciarReproducao(Macro macro)
        {
            if (reproduzindo || gravando) return;
            if (macro.Eventos.Count == 0) { AtualizarStatus("Macro vazio. Grave acoes primeiro.", ACCENT_YELLOW); return; }

            reproduzindo = true;
            macroReproduzindo = macro;
            voltaAtual = 0;

            BeginInvoke(() =>
            {
                btnTestar.Enabled = false;
                btnPararReproducao.Enabled = true;
                btnGravar.Enabled = false;
            });

            Task.Run(() => ExecutarReproducao(macro));
        }

        private void PararReproducao()
        {
            reproduzindo = false;
            macroReproduzindo = null;
            BeginInvoke(() =>
            {
                btnTestar.Enabled = true;
                btnPararReproducao.Enabled = false;
                btnGravar.Enabled = true;
                AtualizarStatus("Reproducao parada", ACCENT_GREEN);
            });
        }

        private void ExecutarReproducao(Macro macro)
        {
            try
            {
                if (macro.AtrasoInicialMs > 0)
                {
                    AtualizarStatus($"Atraso inicial: {macro.AtrasoInicialMs}ms...", ACCENT_YELLOW);
                    DormirCancelavel(macro.AtrasoInicialMs);
                    if (!reproduzindo) return;
                }

                int totalVoltas = macro.Repeticoes == 0 ? int.MaxValue : macro.Repeticoes;
                for (voltaAtual = 1; voltaAtual <= totalVoltas && reproduzindo; voltaAtual++)
                {
                    string voltaStr = macro.Repeticoes == 0 ? $"Volta {voltaAtual} (infinito)" : $"Volta {voltaAtual}/{macro.Repeticoes}";
                    AtualizarStatus($"Rodando: {macro.Name}  \u2022  {voltaStr}", ACCENT_RED);

                    double velocidade = biblioteca.Config.Velocidade;
                    if (velocidade <= 0) velocidade = 1.0;

                    double tempoBase = 0;
                    for (int i = 0; i < macro.Eventos.Count && reproduzindo; i++)
                    {
                        var evt = macro.Eventos[i];
                        double espera = evt.T - tempoBase;
                        if (espera > 0)
                        {
                            int ms = (int)(espera * 1000 / velocidade);
                            DormirCancelavel(ms);
                            if (!reproduzindo) return;
                        }
                        tempoBase = evt.T;
                        ExecutarEvento(evt);
                    }

                    if (reproduzindo && voltaAtual < totalVoltas)
                    {
                        AtualizarStatus($"Intervalo: {macro.IntervaloMs}ms...", ACCENT_YELLOW);
                        DormirCancelavel(macro.IntervaloMs);
                    }
                }
            }
            finally
            {
                BeginInvoke(() =>
                {
                    reproduzindo = false;
                    btnTestar.Enabled = true;
                    btnPararReproducao.Enabled = false;
                    btnGravar.Enabled = true;
                    AtualizarStatus("Reproducao concluida", ACCENT_GREEN);
                });
            }
        }

        private void DormirCancelavel(int ms)
        {
            int dormido = 0;
            while (dormido < ms && reproduzindo)
            {
                int pedaco = Math.Min(50, ms - dormido);
                Thread.Sleep(pedaco);
                dormido += pedaco;
            }
        }

        private void ExecutarEvento(MacroEvent evt)
        {
            switch (evt.Type)
            {
                case "move":
                    Win32.SetCursorPos(evt.X, evt.Y);
                    break;
                case "mouse":
                    Win32.SetCursorPos(evt.X, evt.Y);
                    uint flags = evt.Button switch
                    {
                        "left" => evt.Down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP,
                        "right" => evt.Down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP,
                        "middle" => evt.Down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP,
                        _ => 0
                    };
                    EnviarInputMouse(flags, 0);
                    break;
                case "scroll":
                    Win32.SetCursorPos(evt.X, evt.Y);
                    EnviarInputMouse(Win32.MOUSEEVENTF_WHEEL, evt.Wheel);
                    break;
                case "key":
                    EnviarInputTeclado((ushort)evt.Key, evt.Down);
                    break;
            }
        }

        private void EnviarInputMouse(uint flags, int mouseData)
        {
            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.INPUTUNION { mi = new Win32.MOUSEINPUT { dwFlags = flags, mouseData = mouseData } }
            };
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        private void EnviarInputTeclado(ushort vk, bool down)
        {
            var input = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : Win32.KEYEVENTF_KEYUP } }
            };
            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        // ==================================================================
        // HOTKEYS GLOBAIS
        // ==================================================================

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RegistrarHotkeys(); }

        private void RegistrarHotkeys()
        {
            if (!IsHandleCreated) return;
            foreach (var id in hotkeysRegistrados.Keys) Win32.UnregisterHotKey(Handle, id);
            hotkeysRegistrados.Clear();
            Win32.UnregisterHotKey(Handle, HOTKEY_PANICO_ID);
            Win32.UnregisterHotKey(Handle, HOTKEY_GRAVAR_ID);

            // Panico (ex: Ctrl+F12)
            var (panicoMod, panicoVk) = ParseHotkeyCombo(biblioteca.Config.HotkeyPanico);
            if (panicoVk != 0)
                Win32.RegisterHotKey(Handle, HOTKEY_PANICO_ID, panicoMod | Win32.MOD_NOREPEAT, panicoVk);

            // Gravar (ex: F9)
            uint gravarVk = HotkeyParaVK(biblioteca.Config.HotkeyGravar);
            if (gravarVk != 0)
                Win32.RegisterHotKey(Handle, HOTKEY_GRAVAR_ID, Win32.MOD_NOREPEAT, gravarVk);

            for (int i = 0; i < biblioteca.Macros.Count; i++)
            {
                var macro = biblioteca.Macros[i];
                if (string.IsNullOrEmpty(macro.Hotkey)) continue;
                uint vk = HotkeyParaVK(macro.Hotkey);
                if (vk == 0) continue;
                int id = 1000 + i;
                if (Win32.RegisterHotKey(Handle, id, Win32.MOD_NOREPEAT, vk))
                    hotkeysRegistrados[id] = i;
            }
        }

        private static uint HotkeyParaVK(string hotkey) => hotkey switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B, _ => 0
        };

        private static (uint mod, uint vk) ParseHotkeyCombo(string combo)
        {
            // Suporta "Ctrl+F12", "F9", etc.
            uint mod = 0;
            string key = combo;
            if (combo.StartsWith("Ctrl+", StringComparison.OrdinalIgnoreCase))
            {
                mod = 0x0002; // MOD_CONTROL
                key = combo.Substring(5);
            }
            return (mod, HotkeyParaVK(key));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_PANICO_ID)
                {
                    if (reproduzindo) PararReproducao();
                    if (gravando) PararGravacao();
                    return;
                }
                if (id == HOTKEY_GRAVAR_ID)
                {
                    // F9 toggle gravacao do macro selecionado
                    if (gravando)
                        PararGravacao();
                    else if (!reproduzindo && macroSelecionado != null)
                    {
                        btnGravar.Enabled = false;
                        btnPararGravacao.Enabled = true;
                        Task.Run(() =>
                        {
                            for (int i = 3; i > 0; i--)
                            {
                                AtualizarStatus($"Gravacao comeca em {i}...", ACCENT_YELLOW);
                                Thread.Sleep(1000);
                            }
                            BeginInvoke(IniciarGravacao);
                        });
                    }
                    return;
                }
                if (hotkeysRegistrados.TryGetValue(id, out int macroIdx) && macroIdx >= 0 && macroIdx < biblioteca.Macros.Count)
                {
                    var macro = biblioteca.Macros[macroIdx];
                    if (reproduzindo && macroReproduzindo == macro) PararReproducao();
                    else if (!reproduzindo && !gravando)
                    {
                        lstMacros.SelectedIndex = macroIdx;
                        IniciarReproducao(macro);
                    }
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (reproduzindo) PararReproducao();
            if (gravando) PararGravacao();
            foreach (var id in hotkeysRegistrados.Keys) Win32.UnregisterHotKey(Handle, id);
            Win32.UnregisterHotKey(Handle, HOTKEY_PANICO_ID);
            Win32.UnregisterHotKey(Handle, HOTKEY_GRAVAR_ID);
            MciPlayer.Fechar();
            brasaoImg?.Dispose();
            SalvarBiblioteca();
            base.OnFormClosing(e);
        }
    }
}
