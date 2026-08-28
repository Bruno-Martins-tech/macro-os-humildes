using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.NetworkInformation;
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

        // Volume da musica do WYD (0-1000 do MCI). Default baixinho.
        [JsonPropertyName("volumeMusica")]
        public int VolumeMusica { get; set; } = 150;

        // Onboarding: mostra a tela de boas-vindas so na primeira vez.
        [JsonPropertyName("jaViuBoasVindas")]
        public bool JaViuBoasVindas { get; set; }
    }

    public class Biblioteca
    {
        [JsonPropertyName("macros")]
        public List<Macro> Macros { get; set; } = new();

        [JsonPropertyName("config")]
        public ConfiguracoesApp Config { get; set; } = new();
    }

    public class DcLogEntry
    {
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
        [JsonPropertyName("tipo")]
        public string Tipo { get; set; } = ""; // "dc", "spike", "sessao_inicio", "sessao_fim"
        [JsonPropertyName("pingMs")]
        public int PingMs { get; set; }
        [JsonPropertyName("pingMedio")]
        public int PingMedio { get; set; }
        [JsonPropertyName("tempoOnline")]
        public string TempoOnline { get; set; } = "";
        [JsonPropertyName("otimizacoesAtivas")]
        public int OtimizacoesAtivas { get; set; }
        [JsonPropertyName("detalhes")]
        public string Detalhes { get; set; } = "";
    }

    // ======================================================================
    // WIN32 P/INVOKE
    // ======================================================================

    // ======================================================================
    // CANAL DE DISTRIBUICAO (stable x staging)
    // ======================================================================
    // Controlado pelo simbolo de build STAGING (dotnet build -p:Staging=true).
    // Staging = app isolado: pasta de dados propria (nao mistura macros/config com a stable)
    // e canal de update que inclui pre-releases. A guild nunca ve a staging.
    internal static class Canal
    {
#if STAGING
        public const bool EhStaging = true;
        public const string PastaApp = "MacroSupremes-Staging";
        public const string SufixoTitulo = "   [STAGING]";
#else
        public const bool EhStaging = false;
        public const string PastaApp = "MacroSupremes";
        public const string SufixoTitulo = "";
#endif

        // Raiz de dados em %APPDATA%\<PastaApp>
        public static string DirDados => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), PastaApp);
    }

    // Identidade estavel e anonima do PC: hash do MachineGuid do Windows (nao reversivel).
    // Usada na telemetria e pra amarrar a licenca a uma maquina.
    internal static class MaquinaId
    {
        private static string? _cache;
        public static string Hash()
        {
            if (_cache != null) return _cache;
            string bruto = "";
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                bruto = k?.GetValue("MachineGuid") as string ?? "";
            }
            catch { }
            if (string.IsNullOrEmpty(bruto))
                bruto = Environment.MachineName + "|" + Environment.UserName; // fallback
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("MacroSupremes|" + bruto));
            _cache = Convert.ToHexString(bytes).ToLowerInvariant();
            return _cache;
        }
    }

    // Cliente do backend Cloudflare (telemetria + licenca). Best-effort: nunca trava/quebra o app.
    internal static class Backend
    {
        public const string BaseUrl = "https://macro-supremes.bno-bmartins.workers.dev";

        private static readonly HttpClient http = CriarHttp();
        private static HttpClient CriarHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            h.DefaultRequestHeaders.Add("User-Agent", "MacroSupremes-App");
            return h;
        }

        // Sinal de vida anonimo ao abrir (fire-and-forget).
        public static async Task EnviarHeartbeatAsync(string versao)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    machine = MaquinaId.Hash(),
                    version = versao,
                    channel = Canal.EhStaging ? "staging" : "stable",
                });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await http.PostAsync(BaseUrl + "/heartbeat", content, cts.Token);
            }
            catch { /* offline/erro: telemetria e best-effort, ignora */ }
        }

        // Relatorio de desconexoes (Anti-DC) — anonimo, atrelado a maquina. Best-effort.
        public static async Task EnviarDcReportAsync(int dc, int spike, int pingMedio, int wyd)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    machine = MaquinaId.Hash(),
                    dcCount = dc,
                    spikeCount = spike,
                    pingMedio,
                    wydAbertos = wyd,
                });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await http.PostAsync(BaseUrl + "/dc-report", content, cts.Token);
            }
            catch { }
        }

        // POST de licenca (register/validate). Devolve (ok, motivo). "sem_conexao" = servidor fora do ar.
        public static async Task<(bool ok, string reason)> PostLicencaAsync(string rota, string phone, string senha)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { phone, password = senha, machine = MaquinaId.Hash() });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var resp = await http.PostAsync(BaseUrl + rota, content, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                string reason = root.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "") : "";
                return (ok, reason);
            }
            catch { return (false, "sem_conexao"); }
        }
    }

    // ======================================================================
    // AUTO-UPDATER via GitHub Releases
    // ======================================================================

    // Log dedicado do updater (reusa a pasta de logs do app).
    static class UpdLog
    {
        private static readonly string Dir = Path.Combine(Canal.DirDados, "logs");
        private static readonly string Arquivo = Path.Combine(Dir, "update-log.txt");
        private static readonly object _lock = new();

        public static void W(string msg)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Arquivo, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    static class AutoUpdater
    {
        private const string GITHUB_USER = "Bruno-Martins-tech";
        private const string GITHUB_REPO = "macro-os-humildes";
        private const string CURRENT_VERSION = "1.11.0";
        // Stable pega so o "latest" (exclui pre-release). Staging lista tudo e usa o mais recente (inclui pre-release).
        private static readonly string API_URL = Canal.EhStaging
            ? $"https://api.github.com/repos/{GITHUB_USER}/{GITHUB_REPO}/releases"
            : $"https://api.github.com/repos/{GITHUB_USER}/{GITHUB_REPO}/releases/latest";

        private static readonly HttpClient http = CriarHttp();
        private static HttpClient CriarHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; // download pode ser grande
            h.DefaultRequestHeaders.Add("User-Agent", "MacroSupremes-Updater");
            return h;
        }

        public static string VersaoAtual => CURRENT_VERSION;

        // Caminho REAL do executavel (em single-file, ProcessPath aponta pro apphost correto).
        private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

        public static async Task<(bool temUpdate, string versaoNova, string downloadUrl)?> ChecarAtualizacao()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var json = await http.GetStringAsync(API_URL, cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // No canal staging a API devolve um ARRAY de releases; pega o mais recente (inclui pre-release).
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() == 0) return (false, "", "");
                    root = root[0];
                }

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                string versaoRemota = tagName.TrimStart('v', 'V');

                if (!Version.TryParse(versaoRemota, out var vRemota) ||
                    !Version.TryParse(CURRENT_VERSION, out var vLocal))
                    return null;

                if (vRemota <= vLocal)
                    return (false, "", "");

                // Allowlist do asset: .exe standalone; NUNCA o instalador (setup/install/instalador).
                string downloadUrl = "";
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string nome = asset.GetProperty("name").GetString() ?? "";
                        if (nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && nome.IndexOf("setup", StringComparison.OrdinalIgnoreCase) < 0
                            && nome.IndexOf("install", StringComparison.OrdinalIgnoreCase) < 0
                            && nome.IndexOf("instalador", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                return (true, versaoRemota, downloadUrl);
            }
            catch (Exception ex)
            {
                UpdLog.W("ChecarAtualizacao: " + ex.Message);
                return null;
            }
        }

        public static async Task<bool> BaixarEAtualizar(string downloadUrl, Action<int> onProgress)
        {
            string exe = ExePath;
            string exeTmp = exe + ".update.tmp";
            string exeUpd = exe + ".update";
            try
            {
                using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                long baixado = 0;
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var file = File.Create(exeTmp))
                {
                    var buffer = new byte[81920];
                    int lido;
                    while ((lido = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await file.WriteAsync(buffer, 0, lido);
                        baixado += lido;
                        if (totalBytes > 0) onProgress((int)(baixado * 100 / totalBytes));
                    }
                }

                // Integridade do download (nao de autenticidade da origem — ver nota de seguranca).
                if (totalBytes > 0 && baixado != totalBytes)
                {
                    UpdLog.W($"download truncado: {baixado}/{totalBytes}");
                    TryDelete(exeTmp);
                    return false;
                }
                if (!ArquivoParecePE(exeTmp))
                {
                    UpdLog.W("download nao parece .exe valido (sem MZ ou pequeno demais)");
                    TryDelete(exeTmp);
                    return false;
                }

                // Grava o hash e so entao promove .tmp -> .update
                string sha = CalcularSha256(exeTmp);
                File.WriteAllText(exeUpd + ".sha256", sha);
                TryDelete(exeUpd);
                File.Move(exeTmp, exeUpd);
                UpdLog.W($"update baixado ok ({baixado} bytes)");
                return true;
            }
            catch (Exception ex)
            {
                UpdLog.W("BaixarEAtualizar: " + ex.Message);
                TryDelete(exeTmp);
                return false;
            }
        }

        // Chamado no BOOT (Program.Main), antes de abrir a UI.
        // Se ha um .update valido pendente, faz o swap in-process, relanca e sai.
        // Se falhar em QUALQUER passo, faz rollback e RETORNA (fail-open: a UI abre normal).
        // Renomear o proprio exe em execucao E permitido no Windows (o lock impede escrita, nao rename).
        public static void AplicarUpdateInProcess()
        {
            string exe = ExePath;
            string exeUpd = exe + ".update";
            string exeBak = exe + ".bak";
            string shaFile = exeUpd + ".sha256";

            if (!File.Exists(exeUpd))
            {
                // Sem update pendente: limpa restos orfaos de tentativas anteriores.
                TryDelete(exeBak);
                TryDelete(exe + ".update.tmp");
                TryDelete(shaFile);
                return;
            }

            try
            {
                if (!ArquivoParecePE(exeUpd))
                {
                    UpdLog.W("boot: .update invalido (sem MZ) - descartado");
                    TryDelete(exeUpd); TryDelete(shaFile);
                    return;
                }
                if (File.Exists(shaFile))
                {
                    string esperado = File.ReadAllText(shaFile).Trim();
                    string real = CalcularSha256(exeUpd);
                    if (!string.Equals(esperado, real, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdLog.W("boot: hash do .update nao confere - descartado");
                        TryDelete(exeUpd); TryDelete(shaFile);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                UpdLog.W("boot valida: " + ex.Message);
                return; // fail-open
            }

            bool renomeouExe = false;
            try
            {
                TryDelete(exeBak);
                File.Move(exe, exeBak);   // exe -> .bak (permitido mesmo em execucao)
                renomeouExe = true;
                File.Move(exeUpd, exe);   // .update -> exe
                TryDelete(shaFile);
                UpdLog.W("boot: swap ok - relancando versao nova");

                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                UpdLog.W("boot swap FALHOU: " + ex.Message);
                try
                {
                    if (renomeouExe && !File.Exists(exe) && File.Exists(exeBak))
                    {
                        File.Move(exeBak, exe); // rollback
                        UpdLog.W("boot: rollback do exe feito");
                    }
                }
                catch (Exception ex2) { UpdLog.W("boot rollback FALHOU: " + ex2.Message); }
                // fail-open: retorna; o .update fica pendente pra proxima tentativa.
            }
        }

        // "Aplicar agora" (app aberto): swap externo robusto -> fecha -> troca -> reabre.
        // Se algo falhar, o app CONTINUA aberto e o boot aplica o .update na proxima abertura (backstop).
        // Retorna false se nem conseguiu disparar o swap (app segue aberto).
        public static bool ReiniciarApp()
        {
            string exe = ExePath;
            string exeUpd = exe + ".update";
            if (!File.Exists(exeUpd))
            {
                UpdLog.W("ReiniciarApp: sem .update pendente");
                return false;
            }

            string exeBak = exe + ".bak";
            string shaFile = exeUpd + ".sha256";
            string dirApp = Canal.DirDados;
            string script = Path.Combine(dirApp, "swap-update.ps1");
            string log = Path.Combine(dirApp, "logs", "update-log.txt");

            // Espera o lock liberar por OPEN EXCLUSIVO (nao por PID), try/catch, rollback, log, relanca SEM RunAs.
            string ps =
                "$ErrorActionPreference='Stop'\n" +
                $"$exe='{exe}'\n$upd='{exeUpd}'\n$bak='{exeBak}'\n$sha='{shaFile}'\n$log='{log}'\n" +
                "function L($m){ try{ Add-Content -LiteralPath $log -Value ('['+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')+'] swap: '+$m) }catch{} }\n" +
                "$ok=$false\n" +
                "for($i=0;$i -lt 60;$i++){ try{ $fs=[IO.File]::Open($exe,'Open','ReadWrite','None'); $fs.Close(); $ok=$true; break }catch{ Start-Sleep -Milliseconds 500 } }\n" +
                "if(-not $ok){ L 'exe nunca destravou - abortando sem tocar nos arquivos'; Start-Process -FilePath $exe; exit 1 }\n" +
                "$ren=$false\n" +
                "try{\n" +
                "  if(Test-Path $bak){ Remove-Item -LiteralPath $bak -Force }\n" +
                "  Rename-Item -LiteralPath $exe -NewName ([IO.Path]::GetFileName($bak)); $ren=$true\n" +
                "  Rename-Item -LiteralPath $upd -NewName ([IO.Path]::GetFileName($exe))\n" +
                "  if(Test-Path $sha){ Remove-Item -LiteralPath $sha -Force }\n" +
                "  L 'swap ok'\n" +
                "}catch{\n" +
                "  L ('ERRO: '+$_.Exception.Message)\n" +
                "  if($ren -and -not (Test-Path $exe) -and (Test-Path $bak)){ Rename-Item -LiteralPath $bak -NewName ([IO.Path]::GetFileName($exe)); L 'rollback feito' }\n" +
                "}\n" +
                "Start-Process -FilePath $exe\n";

            try
            {
                Directory.CreateDirectory(dirApp);
                File.WriteAllText(script, ps);
            }
            catch (Exception ex)
            {
                UpdLog.W("ReiniciarApp gravar script: " + ex.Message);
                return false; // app continua aberto; backstop no proximo boot
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                UpdLog.W("ReiniciarApp spawn powershell: " + ex.Message);
                return false; // NAO fecha; backstop no proximo boot aplica o .update
            }

            Environment.Exit(0); // spawn confirmado: fecha pra liberar o lock
            return true;         // inalcancavel
        }

        // --- helpers ---
        private static bool ArquivoParecePE(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 1_000_000) return false; // exe single-file tem dezenas de MB
                using var fs = File.OpenRead(path);
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                return b0 == 0x4D && b1 == 0x5A; // "MZ"
            }
            catch { return false; }
        }

        private static string CalcularSha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
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

    // ======================================================================
    // ANTI-DC — Otimizacoes de rede e processo para reduzir desconexoes
    // ======================================================================
    static class AntiDC
    {
        private static readonly string TCPIP_INTERFACES = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private static readonly string TCPIP_PARAMS = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
        private static readonly string TCPIP6_PARAMS = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
        private static readonly string NIC_CLASS = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002bE10318}";
        private static readonly string[] WYD_NAMES = { "WYD", "WydGlobal", "wyd" };
        // Seam de teste: permite mirar num nome ficticio (ex.: "WYDTEST") sem tocar no WYD real.
        internal static string[]? TestProcessNames;
        private static string[] NomesAlvo => TestProcessNames ?? WYD_NAMES;

        // --- TcpNoDelay ---
        public static bool IsTcpNoDelayAtivo()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES);
                if (root == null) return false;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("TCPNoDelay") is int v && v == 1) return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static void AtivarTcpNoDelay()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    k?.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void DesativarTcpNoDelay()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    k?.DeleteValue("TCPNoDelay", throwOnMissingValue: false);
                }
            }
            catch { }
        }

        // --- TcpAckFrequency ---
        public static bool IsTcpAckFrequencyAtivo()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES);
                if (root == null) return false;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("TcpAckFrequency") is int v && v == 1) return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static void AtivarTcpAckFrequency()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    k?.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void DesativarTcpAckFrequency()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(TCPIP_INTERFACES, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    k?.DeleteValue("TcpAckFrequency", throwOnMissingValue: false);
                }
            }
            catch { }
        }

        // --- KeepAlive Curto ---
        public static bool IsKeepAliveAtivo()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP_PARAMS);
                return k?.GetValue("KeepAliveTime") is int v && v == 60000;
            }
            catch { return false; }
        }

        public static void AtivarKeepAlive()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP_PARAMS, writable: true);
                k?.SetValue("KeepAliveTime", 60000, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static void DesativarKeepAlive()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP_PARAMS, writable: true);
                k?.DeleteValue("KeepAliveTime", throwOnMissingValue: false);
            }
            catch { }
        }

        // --- Desativar IPv6 ---
        public static bool IsIPv6DesativadoAtivo()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP6_PARAMS);
                return k?.GetValue("DisabledComponents") is int v && v == 0xFF;
            }
            catch { return false; }
        }

        public static void AtivarIPv6Desativado()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP6_PARAMS, writable: true);
                k?.SetValue("DisabledComponents", 0xFF, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static void DesativarIPv6Desativado()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(TCPIP6_PARAMS, writable: true);
                k?.DeleteValue("DisabledComponents", throwOnMissingValue: false);
            }
            catch { }
        }

        // --- NIC Power Management ---
        public static bool IsNicPowerMgmtAtivo()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NIC_CLASS);
                if (root == null) return false;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") != null && k.GetValue("PnPCapabilities") is int v && v == 24)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static void AtivarNicPowerMgmt()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NIC_CLASS, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k?.GetValue("DriverDesc") != null)
                        k.SetValue("PnPCapabilities", 24, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void DesativarNicPowerMgmt()
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NIC_CLASS, writable: true);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k?.GetValue("DriverDesc") != null)
                        k.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
                }
            }
            catch { }
        }

        // --- Wi-Fi Power Save ---
        public static bool IsWifiPowerSaveAtivo()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c netsh wlan show settings")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output.Contains("power", StringComparison.OrdinalIgnoreCase) &&
                       output.Contains("disabled", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void AtivarWifiPowerSave()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c netsh wlan set autoconfig enabled=no interface=\"Wi-Fi\" 2>nul & netsh int tcp set global autotuninglevel=disabled 2>nul")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        public static void DesativarWifiPowerSave()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c netsh wlan set autoconfig enabled=yes interface=\"Wi-Fi\" 2>nul & netsh int tcp set global autotuninglevel=normal 2>nul")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        // --- Firewall Whitelist ---
        public static bool IsFirewallWhitelistAtivo()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c netsh advfirewall firewall show rule name=\"WYD Global\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output.Contains("WYD Global", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void AtivarFirewallWhitelist()
        {
            try
            {
                string exeWyd = ExeWydDetectado();
                if (string.IsNullOrEmpty(exeWyd))
                    exeWyd = Path.Combine(PastaPadraoWyd(), "WYD.exe"); // ultimo recurso
                string cmd = $"/c netsh advfirewall firewall add rule name=\"WYD Global\" dir=in action=allow program=\"{exeWyd}\" enable=yes 2>nul & " +
                             $"netsh advfirewall firewall add rule name=\"WYD Global Out\" dir=out action=allow program=\"{exeWyd}\" enable=yes 2>nul";
                var psi = new ProcessStartInfo("cmd.exe", cmd)
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        public static void DesativarFirewallWhitelist()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c netsh advfirewall firewall delete rule name=\"WYD Global\" 2>nul & netsh advfirewall firewall delete rule name=\"WYD Global Out\" 2>nul")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        // Pasta padrao do launcher (fallback quando o WYD nao esta aberto).
        private static string PastaPadraoWyd() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wyd_launcher", "WYD Global");

        // Caminho do WYD.exe descoberto pelo PROCESSO em execucao (robusto a instalacao fora do padrao).
        // Se o WYD nao estiver aberto, cai no local padrao do launcher. "" se nem isso existir.
        public static string ExeWydDetectado()
        {
            var ps = ObterProcessosWyd();
            try
            {
                foreach (var p in ps)
                {
                    try { var exe = p.MainModule?.FileName; if (!string.IsNullOrEmpty(exe)) return exe; }
                    catch { }
                }
            }
            finally { foreach (var p in ps) { try { p.Dispose(); } catch { } } }

            string padrao = Path.Combine(PastaPadraoWyd(), "WYD.exe");
            return File.Exists(padrao) ? padrao : "";
        }

        // login.mp3 do WYD, derivado da pasta do processo (ou do padrao). "" se nao achar.
        public static string MusicaWydDetectada()
        {
            string exe = ExeWydDetectado();
            string? dir = string.IsNullOrEmpty(exe) ? PastaPadraoWyd() : Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
            {
                string mp3 = Path.Combine(dir, "music", "login.mp3");
                if (File.Exists(mp3)) return mp3;
            }
            return "";
        }

        // --- Prioridade Alta ---
        // --- Processos WYD (fonte unica; dedup por Id entre os nomes) ---
        public static List<Process> ObterProcessosWyd()
        {
            var lista = new List<Process>();
            var vistos = new HashSet<int>();
            foreach (var name in NomesAlvo)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (var p in procs)
                {
                    try
                    {
                        if (!vistos.Add(p.Id)) { p.Dispose(); continue; }
                        lista.Add(p);
                    }
                    catch { try { p.Dispose(); } catch { } }
                }
            }
            return lista;
        }

        public static int ContarWyd()
        {
            var ps = ObterProcessosWyd();
            int n = ps.Count;
            foreach (var p in ps) { try { p.Dispose(); } catch { } }
            return n;
        }

        // Estado desejado (o vigia usa isso pra reaplicar em WYD abertos depois)
        private static bool _highPriorityDesejado;
        private static bool _cpuAffinityDesejado;
        public static bool HighPriorityDesejado => _highPriorityDesejado;
        public static bool CpuAffinityDesejado => _cpuAffinityDesejado;

        // --- High Priority (aplica em TODAS as instancias abertas) ---
        public static bool IsHighPriorityAtivo()
        {
            var ps = ObterProcessosWyd();
            try
            {
                foreach (var p in ps)
                    try { if (p.PriorityClass == ProcessPriorityClass.High) return true; } catch { }
                return false;
            }
            finally { foreach (var p in ps) { try { p.Dispose(); } catch { } } }
        }

        public static void AtivarHighPriority()
        {
            _highPriorityDesejado = true;
            AplicarHighPriority();
        }

        private static void AplicarHighPriority()
        {
            var ps = ObterProcessosWyd();
            foreach (var p in ps)
            {
                try { if (p.PriorityClass != ProcessPriorityClass.High) p.PriorityClass = ProcessPriorityClass.High; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        public static void DesativarHighPriority()
        {
            _highPriorityDesejado = false;
            var ps = ObterProcessosWyd();
            foreach (var p in ps)
            {
                try { p.PriorityClass = ProcessPriorityClass.Normal; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        // total de WYD abertos, e quantos estao em prioridade Alta
        public static (int total, int aplicados) StatusPrioridade()
        {
            var ps = ObterProcessosWyd();
            int total = ps.Count, ap = 0;
            foreach (var p in ps)
            {
                try { if (p.PriorityClass == ProcessPriorityClass.High) ap++; } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return (total, ap);
        }

        // --- CPU Affinity (adaptavel: espalha as instancias entre os cores da maquina) ---
        public static bool IsCpuAffinityAtivo()
        {
            long full = AffinityPlan.FullMask(Environment.ProcessorCount);
            var ps = ObterProcessosWyd();
            try
            {
                foreach (var p in ps)
                    try { long m = (long)p.ProcessorAffinity; if (m != 0 && m != full) return true; } catch { }
                return false;
            }
            finally { foreach (var p in ps) { try { p.Dispose(); } catch { } } }
        }

        public static void AtivarCpuAffinity()
        {
            _cpuAffinityDesejado = true;
            AplicarCpuAffinity();
        }

        private static void AplicarCpuAffinity()
        {
            int cores = Environment.ProcessorCount;
            var ps = ObterProcessosWyd();
            ps.Sort((a, b) => a.Id.CompareTo(b.Id)); // ordem estavel = atribuicao estavel entre reaplicacoes
            int total = ps.Count;
            for (int i = 0; i < total; i++)
            {
                try { ps[i].ProcessorAffinity = (IntPtr)AffinityPlan.MaskFor(i, total, cores); }
                catch { }
                finally { try { ps[i].Dispose(); } catch { } }
            }
        }

        public static void DesativarCpuAffinity()
        {
            _cpuAffinityDesejado = false;
            long full = AffinityPlan.FullMask(Environment.ProcessorCount);
            var ps = ObterProcessosWyd();
            foreach (var p in ps)
            {
                try { p.ProcessorAffinity = (IntPtr)full; }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }

        // total de WYD abertos, e quantos tem afinidade personalizada (diferente da mascara cheia)
        public static (int total, int aplicados) StatusAfinidade()
        {
            long full = AffinityPlan.FullMask(Environment.ProcessorCount);
            var ps = ObterProcessosWyd();
            int total = ps.Count, ap = 0;
            foreach (var p in ps)
            {
                try { long m = (long)p.ProcessorAffinity; if (m != 0 && m != full) ap++; } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return (total, ap);
        }

        // Vigia: reaplica prioridade/afinidade nos WYD atuais (inclui os que abriram depois)
        public static void ReaplicarProcessos()
        {
            if (_highPriorityDesejado) AplicarHighPriority();
            if (_cpuAffinityDesejado) AplicarCpuAffinity();
        }

        // Igual a ReaplicarProcessos + StatusPrioridade + StatusAfinidade, mas enumerando os processos
        // UMA vez so (antes eram ate 4 enumeracoes por tick de 3s). Reaplica o desejado e devolve os status.
        public static (int total, int prioAplicados, int cpuAplicados) ReaplicarEObterStatus()
        {
            var ps = ObterProcessosWyd();
            try
            {
                int total = ps.Count;

                if (_cpuAffinityDesejado && total > 0)
                {
                    ps.Sort((a, b) => a.Id.CompareTo(b.Id)); // ordem estavel = atribuicao estavel
                    int cores = Environment.ProcessorCount;
                    for (int i = 0; i < total; i++)
                        try { ps[i].ProcessorAffinity = (IntPtr)AffinityPlan.MaskFor(i, total, cores); } catch { }
                }
                if (_highPriorityDesejado)
                {
                    foreach (var p in ps)
                        try { if (p.PriorityClass != ProcessPriorityClass.High) p.PriorityClass = ProcessPriorityClass.High; } catch { }
                }

                long full = AffinityPlan.FullMask(Environment.ProcessorCount);
                int prio = 0, cpu = 0;
                foreach (var p in ps)
                {
                    try { if (p.PriorityClass == ProcessPriorityClass.High) prio++; } catch { }
                    try { long m = (long)p.ProcessorAffinity; if (m != 0 && m != full) cpu++; } catch { }
                }
                return (total, prio, cpu);
            }
            finally { foreach (var p in ps) { try { p.Dispose(); } catch { } } }
        }

        // --- High Performance Power Plan ---
        public static bool IsHighPerfPlanAtivo()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c powercfg /getactivescheme")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void AtivarHighPerfPlan()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        public static void DesativarHighPerfPlan()
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); p?.WaitForExit(5000);
            }
            catch { }
        }

        // --- Ping ---
        public static async Task<int> PingAsync()
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 2000);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
            }
            catch { return -1; }
        }

        // ======================================================================
        // DC MONITOR — Log de desconexoes, spikes e sessoes
        // ======================================================================

        private static readonly string LogDir = Path.Combine(Canal.DirDados, "logs");
        private static readonly List<int> pingHistory = new();
        private static readonly object pingLock = new();
        private static DateTime? sessaoInicio;
        private static int dcCount;
        private static int spikeCount;
        private static bool wydRodando;

        public static void IniciarSessao()
        {
            sessaoInicio = DateTime.Now;
            dcCount = 0;
            spikeCount = 0;
            pingHistory.Clear();
            wydRodando = IsWydRunning();
            SalvarLog(new DcLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Tipo = "sessao_inicio",
                OtimizacoesAtivas = ContarOtimizacoesAtivas(),
                Detalhes = wydRodando ? "WYD detectado" : "WYD nao encontrado"
            });
        }

        public static bool IsWydRunning()
        {
            foreach (var n in NomesAlvo)
            {
                try
                {
                    var procs = Process.GetProcessesByName(n);
                    bool found = procs.Length > 0;
                    foreach (var p in procs) p.Dispose();
                    if (found) return true;
                }
                catch { }
            }
            return false;
        }

        public static void RegistrarPing(int ms)
        {
            lock (pingLock)
            {
                if (ms > 0) pingHistory.Add(ms);
                if (pingHistory.Count > 1000) pingHistory.RemoveAt(0);
            }

            // Detect spike (>500ms)
            if (ms > 500)
            {
                spikeCount++;
                SalvarLog(new DcLogEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Tipo = "spike",
                    PingMs = ms,
                    PingMedio = PingMedio(),
                    TempoOnline = TempoOnlineStr(),
                    OtimizacoesAtivas = ContarOtimizacoesAtivas(),
                    Detalhes = $"Latencia alta: {ms}ms"
                });
            }

            // Check if WYD process disappeared (DC detection)
            bool wydAgora = IsWydRunning();
            if (wydRodando && !wydAgora)
            {
                dcCount++;
                int ultimoPing = pingHistory.Count > 0 ? pingHistory[^1] : -1;
                string causa = ultimoPing > 300 ? "Ping alto antes da queda (possivel problema de rede)"
                    : ultimoPing > 0 ? "Ping estava normal (possivel bug do servidor/jogo)"
                    : "Sem dados de ping";

                SalvarLog(new DcLogEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Tipo = "dc",
                    PingMs = ultimoPing,
                    PingMedio = PingMedio(),
                    TempoOnline = TempoOnlineStr(),
                    OtimizacoesAtivas = ContarOtimizacoesAtivas(),
                    Detalhes = causa
                });
            }
            wydRodando = wydAgora;
        }

        public static int PingMedio()
        {
            lock (pingLock)
            {
                if (pingHistory.Count == 0) return 0;
                return (int)pingHistory.Average();
            }
        }

        public static int PingMax()
        {
            lock (pingLock)
            {
                if (pingHistory.Count == 0) return 0;
                return pingHistory.Max();
            }
        }

        public static string TempoOnlineStr()
        {
            if (sessaoInicio == null) return "00:00:00";
            var elapsed = DateTime.Now - sessaoInicio.Value;
            return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        public static int DcCount => dcCount;
        public static int SpikeCount => spikeCount;

        public static int ContarOtimizacoesAtivas()
        {
            int count = 0;
            try { if (IsTcpNoDelayAtivo()) count++; } catch { }
            try { if (IsTcpAckFrequencyAtivo()) count++; } catch { }
            try { if (IsKeepAliveAtivo()) count++; } catch { }
            try { if (IsIPv6DesativadoAtivo()) count++; } catch { }
            try { if (IsNicPowerMgmtAtivo()) count++; } catch { }
            try { if (IsWifiPowerSaveAtivo()) count++; } catch { }
            try { if (IsFirewallWhitelistAtivo()) count++; } catch { }
            try { if (IsHighPriorityAtivo()) count++; } catch { }
            try { if (IsCpuAffinityAtivo()) count++; } catch { }
            try { if (IsHighPerfPlanAtivo()) count++; } catch { }
            return count;
        }

        private static void SalvarLog(DcLogEntry entry)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                string arquivo = Path.Combine(LogDir, $"dc-log-{DateTime.Now:yyyy-MM-dd}.json");
                var logs = new List<DcLogEntry>();
                if (File.Exists(arquivo))
                {
                    string json = File.ReadAllText(arquivo);
                    logs = JsonSerializer.Deserialize<List<DcLogEntry>>(json) ?? new List<DcLogEntry>();
                }
                logs.Add(entry);
                File.WriteAllText(arquivo, JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void FinalizarSessao()
        {
            SalvarLog(new DcLogEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Tipo = "sessao_fim",
                PingMedio = PingMedio(),
                TempoOnline = TempoOnlineStr(),
                OtimizacoesAtivas = ContarOtimizacoesAtivas(),
                Detalhes = $"DCs: {dcCount}, Spikes: {spikeCount}, Ping max: {PingMax()}ms"
            });
        }

        public static string GerarRelatorio()
        {
            string hoje = DateTime.Now.ToString("yyyy-MM-dd");
            string arquivo = Path.Combine(LogDir, $"dc-log-{hoje}.json");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("  RELATORIO ANTI-DC - MACRO SUPREMUS");
            sb.AppendLine($"  Data: {hoje}");
            sb.AppendLine("========================================");
            sb.AppendLine();

            // Current session stats
            sb.AppendLine("--- SESSAO ATUAL ---");
            sb.AppendLine($"Tempo online: {TempoOnlineStr()}");
            sb.AppendLine($"Ping medio: {PingMedio()}ms");
            sb.AppendLine($"Ping maximo: {PingMax()}ms");
            sb.AppendLine($"Desconexoes (DCs): {dcCount}");
            sb.AppendLine($"Picos de latencia (>500ms): {spikeCount}");
            sb.AppendLine($"Otimizacoes ativas: {ContarOtimizacoesAtivas()}");
            sb.AppendLine();

            // Load today's log
            if (File.Exists(arquivo))
            {
                try
                {
                    string json = File.ReadAllText(arquivo);
                    var logs = JsonSerializer.Deserialize<List<DcLogEntry>>(json) ?? new List<DcLogEntry>();

                    int totalDCs = logs.Count(l => l.Tipo == "dc");
                    int totalSpikes = logs.Count(l => l.Tipo == "spike");
                    int sessoes = logs.Count(l => l.Tipo == "sessao_inicio");

                    sb.AppendLine("--- HISTORICO DO DIA ---");
                    sb.AppendLine($"Sessoes abertas: {sessoes}");
                    sb.AppendLine($"Total de DCs: {totalDCs}");
                    sb.AppendLine($"Total de spikes: {totalSpikes}");
                    sb.AppendLine();

                    // List DCs with details
                    var dcs = logs.Where(l => l.Tipo == "dc").ToList();
                    if (dcs.Count > 0)
                    {
                        sb.AppendLine("--- DETALHES DOS DCs ---");
                        foreach (var dc in dcs)
                        {
                            sb.AppendLine($"[{dc.Timestamp}] Ping: {dc.PingMs}ms | Media: {dc.PingMedio}ms | Online: {dc.TempoOnline} | Anti-DC: {dc.OtimizacoesAtivas} otim.");
                            sb.AppendLine($"  Causa provavel: {dc.Detalhes}");
                        }
                        sb.AppendLine();
                    }

                    // List spikes
                    var spikes = logs.Where(l => l.Tipo == "spike").TakeLast(10).ToList();
                    if (spikes.Count > 0)
                    {
                        sb.AppendLine("--- ULTIMOS PICOS DE LATENCIA ---");
                        foreach (var spike in spikes)
                        {
                            sb.AppendLine($"[{spike.Timestamp}] {spike.PingMs}ms (media era {spike.PingMedio}ms)");
                        }
                    }
                }
                catch { sb.AppendLine("Erro ao ler log do dia."); }
            }
            else
            {
                sb.AppendLine("Nenhum log encontrado para hoje.");
            }

            sb.AppendLine();
            sb.AppendLine("========================================");
            sb.AppendLine("Copie este relatorio e envie ao suporte.");
            sb.AppendLine("========================================");

            return sb.ToString();
        }

        public static string CaminhoLogDir => LogDir;
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

    // Helpers graficos compartilhados (evita duplicar codigo de desenho)
    internal static class Gfx
    {
        // Clareia/escurece uma cor (delta em cada canal). Usado no gradiente sutil dos cards.
        public static Color Shift(Color c, int delta)
        {
            int r = Math.Clamp(c.R + delta, 0, 255);
            int g = Math.Clamp(c.G + delta, 0, 255);
            int b = Math.Clamp(c.B + delta, 0, 255);
            return Color.FromArgb(c.A, r, g, b);
        }

        // Caminho de retangulo com cantos arredondados (fonte unica; antes duplicado em 3 lugares)
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

    // Painel com cantos arredondados e fundo semi-transparente (card)
    public class CardPanel : Panel
    {
        public int Radius { get; set; } = 12;
        public Color CardColor { get; set; } = Color.FromArgb(38, 40, 48);

        // Cor da borda hairline (calculada a partir da cor do card, um degrau mais clara)
        public Color BorderColor { get; set; } = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = ClientRectangle;

            // Fundo com gradiente vertical MUITO sutil (topo levemente mais claro) = volume/profundidade
            using (var path = Gfx.RoundedRect(rect, Radius))
            using (var brush = new LinearGradientBrush(
                new Point(0, rect.Top), new Point(0, rect.Bottom + 1),
                Gfx.Shift(CardColor, 8), Gfx.Shift(CardColor, -6)))
            {
                g.FillPath(brush, path);
            }

            // Borda hairline de 1px por dentro = separa a camada de conteudo do fundo
            var borda = BorderColor == Color.Empty ? Gfx.Shift(CardColor, 26) : BorderColor;
            using var bpath = Gfx.RoundedRect(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), Radius);
            using var pen = new Pen(borda, 1f);
            g.DrawPath(pen, bpath);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // nao pintar background padrao
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

            using var path = Gfx.RoundedRect(rect, Radius);
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
            using var textBrush = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(124, 126, 134));
            g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), sf);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressing = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressing = false; Invalidate(); base.OnMouseUp(e); }
    }

    // ======================================================================
    // FORMULARIO PRINCIPAL
    // ======================================================================

    public class MainForm : Form
    {
        private const string DISCORD_URL = "https://discord.gg/qWZhhXKxj";

        // Cores do tema
        private static readonly Color BG_DARK = Color.FromArgb(18, 18, 24);
        private static readonly Color BG_CARD = Color.FromArgb(28, 30, 38);
        private static readonly Color BG_INPUT = Color.FromArgb(38, 40, 50);
        private static readonly Color ACCENT_GREEN = Color.FromArgb(76, 217, 100);
        private static readonly Color ACCENT_RED = Color.FromArgb(255, 69, 58);
        private static readonly Color ACCENT_BLUE = Color.FromArgb(88, 101, 242);
        private static readonly Color ACCENT_YELLOW = Color.FromArgb(255, 214, 10);
        // Acento PRIMARIO / identidade (dourado WYD). Verde/vermelho/amarelo = so estado.
        private static readonly Color ACCENT_GOLD = Color.FromArgb(212, 175, 55);
        private static readonly Color TEXT_PRIMARY = Color.FromArgb(240, 240, 245);
        // Contraste melhorado (WCAG AA) — antes 142/90 ficavam quase ilegiveis no fundo escuro.
        private static readonly Color TEXT_SECONDARY = Color.FromArgb(176, 178, 188);
        private static readonly Color TEXT_DIM = Color.FromArgb(138, 140, 150);

        private static readonly string AppDataDir = Canal.DirDados;
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
        // Sinalizado ao parar: acorda a espera na hora (sem polling de 50ms) e deixa o timing exato
        private readonly ManualResetEventSlim sinalParar = new(false);

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
        private Label lblParamTitulo = null!;
        private Label lblStatus = null!;
        private Panel pnlStatusBar = null!;
        private ModernButton btnGravar = null!;
        private ModernButton btnPararGravacao = null!;
        private ModernButton btnTestar = null!;
        private ModernButton btnPararReproducao = null!;
        private Label lblEstadoGravacao = null!;

        // Overlay de contagem regressiva (3..2..1) antes de gravar
        private Panel pnlOverlay = null!;
        private string overlayTexto = "";
        private string overlaySub = "";
        private Color overlayCor = Color.FromArgb(212, 175, 55);

        // Sinal de REC (faixa vermelha no topo + titulo [REC]) enquanto grava
        private Panel pnlRec = null!;
        private string tituloBase = "";
        private string abaAtiva = "macros"; // p/ o underline dourado da aba ativa

        // Abas
        private ModernButton btnTabMacros = null!;
        private ModernButton btnTabTutorial = null!;
        private ModernButton btnTabConfig = null!;
        private Panel pnlTabs = null!;
        private Panel pnlMacros = null!;
        private Panel pnlTutorial = null!;
        private Panel pnlConfig = null!;

        // Anti-DC
        private ModernButton btnTabAntiDC = null!;
        private Panel pnlAntiDC = null!;
        private Label lblPingValue = null!;
        private Label lblOptCount = null!;
        private Label lblWydStatus = null!;
        private System.Windows.Forms.Timer pingTimer = null!;
        private int dcReportTicks; // throttle do envio de relatorio de DC (tick de 3s)
        private Label lblTempoOnline = null!;
        private Label lblDcCount = null!;
        private Label lblSpikeCount = null!;
        private Label lblPingMedio = null!;

        private bool carregandoCampos;
        private bool musicaMutada;

        // Runas nordicas para decoracao (referencia ao WYD)
        private const string RUNAS = "\u16A0\u16A2\u16A6\u16A8\u16B1\u16B7\u16C1\u16C7\u16D2\u16DE";

        // Brasao da guild
        private Image? brasaoImg;

        // Carrega o brasao: 1o do recurso EMBUTIDO no exe (portavel), senao do arquivo ao lado.
        private static Image? CarregarBrasao()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var nome = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("brasao.jpg", StringComparison.OrdinalIgnoreCase));
                if (nome != null)
                {
                    using var st = asm.GetManifestResourceStream(nome);
                    if (st != null) return Image.FromStream(st);
                }
            }
            catch { }
            try
            {
                string p = Path.Combine(AppContext.BaseDirectory, "brasao.jpg");
                if (File.Exists(p)) return Image.FromFile(p);
            }
            catch { }
            return null;
        }

        public MainForm()
        {
            Text = "MACRO \u2022 SUPREMUS  \u2014  With Your Destiny" + Canal.SufixoTitulo;
            tituloBase = Text;
            ClientSize = new Size(620, 720);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG_DARK;
            ForeColor = TEXT_PRIMARY;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9);

            // Carregar brasao da guild (embutido no exe; fallback pro arquivo ao lado)
            brasaoImg = CarregarBrasao();

            CriarUI();
            CarregarBiblioteca();
            AtualizarListaMacros();
            MostrarAba("macros");
            IniciarMusica();
            ChecarAtualizacaoAsync();
            AntiDC.IniciarSessao();
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
                if (!AutoUpdater.ReiniciarApp())
                    MessageBox.Show(
                        "Nao consegui reiniciar automaticamente.\nFeche e abra o app de novo — a atualizacao sera aplicada na proxima abertura.",
                        "Reinicie manualmente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

                // Wordmark da guilda ao fundo (sutil): profundidade + cara "brasonada" medieval
                using (var fontWm = new Font("Segoe UI", 30, FontStyle.Bold | FontStyle.Italic))
                using (var brWm = new SolidBrush(Color.FromArgb(20, 212, 175, 55)))
                    g.DrawString("SUPREMUS", fontWm, brWm, pnlHeader.Width - 305, 22);

                // Linha de acento dourada embaixo (referencia ao ouro WYD)
                using var goldPen = new Pen(Color.FromArgb(110, 212, 175, 55), 2);
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
                g.DrawString("MACRO \u2022 SUPREMUS", fontTitulo, brushGoldShadow, textX + 1, by + 5);
                using var brushTitle = new SolidBrush(TEXT_PRIMARY);
                g.DrawString("MACRO \u2022 SUPREMUS", fontTitulo, brushTitle, textX, by + 4);

                // Subtitulo (uma linha so, enxuto)
                using var fontSub = new Font("Segoe UI", 9);
                using var brushSub = new SolidBrush(Color.FromArgb(212, 175, 55));
                g.DrawString("Guilda Supremus \u2022 WYD Server 3", fontSub, brushSub, textX + 2, by + 34);

                // Runas nordicas (cara medieval), sutis em dourado. Segoe UI Historic cobre o bloco Runic.
                using var fontRune = new Font("Segoe UI Historic", 11);
                using var brushRune = new SolidBrush(Color.FromArgb(95, 212, 175, 55));
                g.DrawString(RUNAS, fontRune, brushRune, textX + 2, by + 52);

                // Versao (canto superior direito)
                using var fontVer = new Font("Segoe UI", 7.5f);
                using var brushVer = new SolidBrush(TEXT_DIM);
                g.DrawString($"v{AutoUpdater.VersaoAtual}{Canal.SufixoTitulo}", fontVer, brushVer, pnlHeader.Width - 90, 8);
            };
            Controls.Add(pnlHeader);

            // --- ABAS ---
            pnlTabs = new Panel { Location = new Point(0, 100), Size = new Size(620, 42), BackColor = Color.FromArgb(22, 24, 30) };
            // Underline dourado sob a aba ativa (visual de "tab" moderno)
            pnlTabs.Paint += (s, e) =>
            {
                ModernButton? ativo = abaAtiva switch
                {
                    "macros" => btnTabMacros,
                    "tutorial" => btnTabTutorial,
                    "config" => btnTabConfig,
                    "antidc" => btnTabAntiDC,
                    _ => null
                };
                if (ativo != null)
                {
                    using var br = new SolidBrush(ACCENT_GOLD);
                    e.Graphics.FillRectangle(br, ativo.Left, pnlTabs.Height - 3, ativo.Width, 3);
                }
            };
            Controls.Add(pnlTabs);

            btnTabMacros = new ModernButton
            {
                Text = "\u2694 MACROS",
                Location = new Point(8, 5),
                Size = new Size(90, 32),
                BaseColor = ACCENT_GOLD,
                HoverColor = Color.FromArgb(226, 190, 78),
                ForeColor = Color.FromArgb(10, 10, 10),
                Radius = 6
            };
            btnTabMacros.Click += (s, e) => MostrarAba("macros");
            pnlTabs.Controls.Add(btnTabMacros);

            btnTabTutorial = new ModernButton
            {
                Text = "\u2139 COMO USAR",
                Location = new Point(102, 5),
                Size = new Size(100, 32),
                BaseColor = Color.FromArgb(45, 47, 55),
                HoverColor = Color.FromArgb(60, 62, 72),
                Radius = 6
            };
            btnTabTutorial.Click += (s, e) => MostrarAba("tutorial");
            pnlTabs.Controls.Add(btnTabTutorial);

            btnTabConfig = new ModernButton
            {
                Text = "\u2699 CONFIG",
                Location = new Point(206, 5),
                Size = new Size(85, 32),
                BaseColor = Color.FromArgb(45, 47, 55),
                HoverColor = Color.FromArgb(60, 62, 72),
                Radius = 6
            };
            btnTabConfig.Click += (s, e) => MostrarAba("config");
            pnlTabs.Controls.Add(btnTabConfig);

            btnTabAntiDC = new ModernButton
            {
                Text = "\uD83D\uDEE1 ANTI-DC",
                Location = new Point(295, 5),
                Size = new Size(105, 32),
                BaseColor = Color.FromArgb(45, 47, 55),
                HoverColor = Color.FromArgb(60, 62, 72),
                Radius = 6,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            btnTabAntiDC.Click += (s, e) => MostrarAba("antidc");
            pnlTabs.Controls.Add(btnTabAntiDC);

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
                    if (!AutoUpdater.ReiniciarApp())
                        MessageBox.Show(
                            "Nao consegui reiniciar automaticamente.\nFeche e abra o app de novo — a atualizacao sera aplicada na proxima abertura.",
                            "Reinicie manualmente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

            // --- PAGINA ANTI-DC ---
            pnlAntiDC = new Panel { Location = new Point(0, 142), Size = new Size(620, 468), BackColor = BG_DARK, Visible = false };
            Controls.Add(pnlAntiDC);
            CriarPaginaAntiDC();

            // --- STATUS BAR ---
            pnlStatusBar = new Panel { Location = new Point(0, 610), Size = new Size(620, 40), BackColor = Color.FromArgb(22, 24, 30) };
            pnlStatusBar.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(40, ACCENT_GREEN), 1);
                e.Graphics.DrawLine(pen, 0, 0, pnlStatusBar.Width, 0);
            };
            Controls.Add(pnlStatusBar);

            // Indicador de status (bolinha colorida + texto)
            var pnlDot = new Panel { Location = new Point(16, 11), Size = new Size(16, 16) };
            pnlDot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color dotColor = reproduzindo ? ACCENT_RED : gravando ? ACCENT_YELLOW : ACCENT_GREEN;
                // Halo suave em volta (leitura mais facil, inclusive p/ daltonico) + dot maior
                using (var halo = new SolidBrush(Color.FromArgb(55, dotColor)))
                    e.Graphics.FillEllipse(halo, 0, 0, 15, 15);
                using var brush = new SolidBrush(dotColor);
                e.Graphics.FillEllipse(brush, 3, 3, 9, 9);
            };
            pnlStatusBar.Controls.Add(pnlDot);

            lblStatus = new Label
            {
                Text = "Pronto",
                Location = new Point(38, 10),
                Size = new Size(236, 20),
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Tag = pnlDot // guardar referencia pra repintar o dot
            };
            pnlStatusBar.Controls.Add(lblStatus);

            // Botao FIXO do Hack de login (sempre visivel; estado ON/OFF bem claro,
            // porque ele mexe no proxy do sistema e nao pode ficar ligado sem querer).
            bool hackAtivo = ProxyHack.IsAtivo();
            var btnHackFixo = new ModernButton
            {
                Text = hackAtivo ? "⚡ Entrar no servidor lotado: ATIVO" : "⚡ Entrar no servidor lotado: OFF",
                Location = new Point(286, 6),
                Size = new Size(318, 28),
                BaseColor = hackAtivo ? Color.FromArgb(40, 140, 40) : Color.FromArgb(52, 44, 30),
                HoverColor = hackAtivo ? Color.FromArgb(50, 170, 50) : Color.FromArgb(70, 58, 38),
                AccentColor = hackAtivo ? ACCENT_GREEN : Color.FromArgb(150, 90, 30),
                Radius = 6,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            btnHackFixo.Click += (s, e) =>
            {
                if (ProxyHack.IsAtivo())
                {
                    ProxyHack.Desativar();
                    btnHackFixo.Text = "⚡ Entrar no servidor lotado: OFF";
                    btnHackFixo.BaseColor = Color.FromArgb(52, 44, 30);
                    btnHackFixo.HoverColor = Color.FromArgb(70, 58, 38);
                    btnHackFixo.AccentColor = Color.FromArgb(150, 90, 30);
                    AtualizarStatus("Entrada normal restaurada.", ACCENT_GREEN);
                }
                else
                {
                    ProxyHack.Ativar();
                    btnHackFixo.Text = "⚡ Entrar no servidor lotado: ATIVO";
                    btnHackFixo.BaseColor = Color.FromArgb(40, 140, 40);
                    btnHackFixo.HoverColor = Color.FromArgb(50, 170, 50);
                    btnHackFixo.AccentColor = ACCENT_GREEN;
                    AtualizarStatus("Ativado! Entre no WYD e depois desligue aqui.", ACCENT_YELLOW);
                }
                btnHackFixo.Invalidate();
            };
            pnlStatusBar.Controls.Add(btnHackFixo);

            // --- RODAPE ---
            var pnlRodape = new Panel { Location = new Point(0, 650), Size = new Size(620, 70), BackColor = Color.FromArgb(16, 16, 22) };
            Controls.Add(pnlRodape);

            // Botoes de acesso rapido
            var btnDiscord = new ModernButton
            {
                Text = "Discord",
                Location = new Point(16, 8),
                Size = new Size(90, 28),
                BaseColor = Color.FromArgb(88, 101, 242),
                HoverColor = Color.FromArgb(105, 118, 255),
                ForeColor = Color.White,
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
                BaseColor = Color.FromArgb(196, 124, 38),
                HoverColor = Color.FromArgb(220, 142, 46),
                ForeColor = Color.White,
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
                BaseColor = Color.FromArgb(56, 132, 68),
                HoverColor = Color.FromArgb(66, 156, 80),
                ForeColor = Color.White,
                Radius = 6,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            btnUpdatesWyd.Click += (s, e) =>
                Process.Start(new ProcessStartInfo("https://wydglobal.raidhut.com/pt-br/3578") { UseShellExecute = true });
            pnlRodape.Controls.Add(btnUpdatesWyd);

            var lblCreditos = new Label
            {
                Text = "Criado por MartinS- \u2022 Supremus \u2022 Server 3",
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

            // --- OVERLAY de contagem regressiva (cobre a area de conteudo) ---
            pnlOverlay = new Panel
            {
                Location = new Point(0, 142),
                Size = new Size(620, 468),
                BackColor = Color.FromArgb(13, 13, 18),
                Visible = false
            };
            pnlOverlay.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var full = new RectangleF(0, 0, pnlOverlay.Width, pnlOverlay.Height);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var big = new Font("Segoe UI", 96, FontStyle.Bold))
                using (var brBig = new SolidBrush(overlayCor))
                    g.DrawString(overlayTexto, big, brBig, new RectangleF(0, -30, pnlOverlay.Width, pnlOverlay.Height), sf);
                if (!string.IsNullOrEmpty(overlaySub))
                {
                    using var f2 = new Font("Segoe UI", 12);
                    using var br2 = new SolidBrush(TEXT_SECONDARY);
                    g.DrawString(overlaySub, f2, br2, new RectangleF(0, pnlOverlay.Height / 2f + 70, pnlOverlay.Width, 30), sf);
                }
            };
            Controls.Add(pnlOverlay);

            // Faixa vermelha de REC no topo (fina), visivel so enquanto grava
            pnlRec = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(620, 4),
                BackColor = ACCENT_RED,
                Visible = false
            };
            Controls.Add(pnlRec);
        }

        // Liga/desliga o sinal de REC (faixa vermelha + titulo). Roda na thread da UI.
        private void SinalRec(bool on)
        {
            if (InvokeRequired) { BeginInvoke(() => SinalRec(on)); return; }
            pnlRec.Visible = on;
            if (on) pnlRec.BringToFront();
            Text = on ? "● REC  —  gravando (ESC para parar)" : tituloBase;
        }

        private void MostrarOverlay(string txt, string sub, Color cor)
        {
            if (InvokeRequired) { BeginInvoke(() => MostrarOverlay(txt, sub, cor)); return; }
            overlayTexto = txt; overlaySub = sub; overlayCor = cor;
            pnlOverlay.Visible = true;
            pnlOverlay.BringToFront();
            pnlOverlay.Invalidate();
        }

        private void EsconderOverlay()
        {
            if (InvokeRequired) { BeginInvoke(EsconderOverlay); return; }
            pnlOverlay.Visible = false;
        }

        // Countdown 3..2..1 (overlay) e entao inicia a gravacao. Fonte unica p/ botao e hotkey F9.
        private void IniciarContagemEGravar()
        {
            if (gravando || reproduzindo) return;
            btnGravar.Enabled = false;
            btnPararGravacao.Enabled = true;
            Task.Run(() =>
            {
                for (int i = 3; i > 0; i--)
                {
                    MostrarOverlay(i.ToString(), "prepare-se pra gravar...", ACCENT_GOLD);
                    Thread.Sleep(1000);
                }
                MostrarOverlay("JÁ!", "gravando seus cliques", ACCENT_RED);
                Thread.Sleep(350);
                BeginInvoke(() => { EsconderOverlay(); IniciarGravacao(); });
            });
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

            // Card direito (topo) — Parametros do macro selecionado
            var cardConfig = new CardPanel
            {
                Location = new Point(260, 10),
                Size = new Size(344, 176),
                CardColor = BG_CARD
            };
            pnlMacros.Controls.Add(cardConfig);

            lblParamTitulo = new Label
            {
                Text = "PARAMETROS DO MACRO",
                Location = new Point(16, 14),
                Size = new Size(312, 18),
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            cardConfig.Controls.Add(lblParamTitulo);

            // Campos
            int lx = 16, rx = 190, y = 42, gap = 38;

            AddLabel(cardConfig, "Tecla de atalho", lx, y);
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
            AddLabel(cardConfig, "Repetir (0 = sempre)", lx, y);
            nudRepeticoes = CriarNumeric(new Point(rx, y - 3), 0, 99999, 0);
            nudRepeticoes.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudRepeticoes);

            y += gap;
            AddLabel(cardConfig, "Pausa entre repetições (ms)", lx, y);
            nudIntervalo = CriarNumeric(new Point(rx, y - 3), 0, 999999, 1000);
            nudIntervalo.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudIntervalo);

            y += gap;
            AddLabel(cardConfig, "Espera pra começar (ms)", lx, y);
            nudAtraso = CriarNumeric(new Point(rx, y - 3), 0, 999999, 0);
            nudAtraso.ValueChanged += CampoAlterado;
            cardConfig.Controls.Add(nudAtraso);

            // Velocidade fica na aba CONFIG (é global, vale pra todos os macros) — nao mais aqui.
            // A contagem de acoes gravadas foi pro card GRAVACAO, junto de quem grava.

            // Card direito (baixo) — Gravacao e reproducao
            var cardAcoes = new CardPanel
            {
                Location = new Point(260, 192),
                Size = new Size(344, 210),
                CardColor = BG_CARD
            };
            pnlMacros.Controls.Add(cardAcoes);

            var lblTitAcoes = new Label
            {
                Text = "GRAVAÇÃO",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAcoes.Controls.Add(lblTitAcoes);

            lblAcoes = new Label
            {
                Text = "0 ações gravadas",
                Location = new Point(16, 34),
                AutoSize = true,
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAcoes.Controls.Add(lblAcoes);

            btnGravar = new ModernButton
            {
                Text = $"\u2460  Gravar ({biblioteca.Config.HotkeyGravar})",
                Location = new Point(16, 62),
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
                Location = new Point(170, 62),
                Size = new Size(155, 34),
                Enabled = false,
                Radius = 8
            };
            btnPararGravacao.Click += BtnPararGravacao_Click;
            cardAcoes.Controls.Add(btnPararGravacao);

            btnTestar = new ModernButton
            {
                Text = "\u2461  Testar",
                Location = new Point(16, 104),
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
                Location = new Point(170, 104),
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
                Location = new Point(16, 142),
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
                Text = "\u2462  Salvar",
                Location = new Point(16, 168),
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
                Location = new Point(134, 168),
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

            // Card atalhos rapidos (rodape da tela)
            var cardAtalhos = new CardPanel
            {
                Location = new Point(16, 410),
                Size = new Size(588, 48),
                CardColor = Color.FromArgb(24, 26, 34)
            };
            pnlMacros.Controls.Add(cardAtalhos);

            var lblAtalhos = new Label
            {
                Text = "ATALHOS GLOBAIS",
                Location = new Point(16, 8),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 7, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardAtalhos.Controls.Add(lblAtalhos);

            var lblAtalhosInfo = new Label
            {
                Text = $"F5-F8  macro   {biblioteca.Config.HotkeyGravar}  gravar   {biblioteca.Config.HotkeyPanico}  parar tudo",
                Location = new Point(16, 26),
                Size = new Size(500, 18),
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
            AddLabel(cardAtalhos, "Emergência (para tudo)", lx, y);
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
                Value = Math.Clamp((int)(biblioteca.Config.Velocidade * 2), 1, 10),
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
                SalvarBibliotecaDebounced();
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

            // O "Hack Login Server Full" saiu daqui: virou botao FIXO na barra de status (sempre visivel).

            // Card — Som / Musica
            var cardSom = new CardPanel
            {
                Location = new Point(16, 370),
                Size = new Size(588, 92),
                CardColor = BG_CARD
            };
            pnlConfig.Controls.Add(cardSom);

            var lblTitSom = new Label
            {
                Text = "SOM / MUSICA",
                Location = new Point(16, 14),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            cardSom.Controls.Add(lblTitSom);

            var lblDescSom = new Label
            {
                Text = "Volume da musica do WYD (login.mp3). Use o botao ♫ da barra de cima para mutar.",
                Location = new Point(16, 36),
                Size = new Size(550, 18),
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            };
            cardSom.Controls.Add(lblDescSom);

            var trkVolume = new TrackBar
            {
                Location = new Point(16, 58),
                Size = new Size(420, 30),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(biblioteca.Config.VolumeMusica / 10, 0, 100),
                TickFrequency = 10,
                SmallChange = 5,
                LargeChange = 10,
                BackColor = BG_CARD
            };
            var lblVolValor = new Label
            {
                Text = $"{Math.Clamp(biblioteca.Config.VolumeMusica / 10, 0, 100)}%",
                Location = new Point(445, 60),
                AutoSize = true,
                ForeColor = ACCENT_YELLOW,
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            trkVolume.ValueChanged += (s, ev) =>
            {
                int vol1000 = trkVolume.Value * 10; // 0-100 -> 0-1000 (escala do MCI)
                biblioteca.Config.VolumeMusica = vol1000;
                lblVolValor.Text = $"{trkVolume.Value}%";
                if (!musicaMutada) MciPlayer.SetVolume(vol1000); // aplica na hora
                SalvarBibliotecaDebounced();
            };
            cardSom.Controls.Add(trkVolume);
            cardSom.Controls.Add(lblVolValor);
        }

        private void CriarPaginaAntiDC()
        {
            // --- Hero Card ---
            var heroCard = new CardPanel
            {
                Location = new Point(16, 10),
                Size = new Size(588, 130),
                CardColor = BG_CARD
            };
            pnlAntiDC.Controls.Add(heroCard);

            heroCard.Controls.Add(new Label
            {
                Text = "PROTECAO ANTI-DISCONNECT",
                Location = new Point(16, 12),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                BackColor = Color.Transparent
            });

            heroCard.Controls.Add(new Label
            {
                Text = "Otimiza rede e processo pra reduzir quedas no WYD",
                Location = new Point(16, 36),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            });

            var btnOtimizarTudo = new ModernButton
            {
                Text = "\u26A1 Otimizar Tudo",
                Location = new Point(16, 58),
                Size = new Size(170, 32),
                BaseColor = Color.FromArgb(30, 100, 40),
                HoverColor = Color.FromArgb(40, 130, 50),
                AccentColor = ACCENT_GREEN,
                Radius = 6,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            heroCard.Controls.Add(btnOtimizarTudo);

            var btnReverterTudo = new ModernButton
            {
                Text = "\u21A9 Reverter Tudo",
                Location = new Point(196, 58),
                Size = new Size(170, 32),
                BaseColor = Color.FromArgb(100, 30, 30),
                HoverColor = Color.FromArgb(130, 40, 40),
                AccentColor = ACCENT_RED,
                Radius = 6,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            heroCard.Controls.Add(btnReverterTudo);

            // Ping display
            heroCard.Controls.Add(new Label
            {
                Text = "Ping:",
                Location = new Point(16, 102),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            });

            lblPingValue = new Label
            {
                Text = "Medindo...",
                Location = new Point(56, 102),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            heroCard.Controls.Add(lblPingValue);

            lblOptCount = new Label
            {
                Text = "0 de 10 otimizacoes ativas",
                Location = new Point(200, 102),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8.5f),
                BackColor = Color.Transparent
            };
            heroCard.Controls.Add(lblOptCount);

            // Status dos WYD abertos (contagem + prioridade/afinidade aplicadas por instancia)
            lblWydStatus = new Label
            {
                Text = "WYD: --",
                Location = new Point(380, 102),
                AutoSize = true,
                ForeColor = TEXT_DIM,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            heroCard.Controls.Add(lblWydStatus);

            // --- Scrollable area ---
            var pnlScroll = new Panel
            {
                Location = new Point(16, 148),
                Size = new Size(588, 310),
                AutoScroll = true,
                BackColor = BG_DARK
            };
            pnlAntiDC.Controls.Add(pnlScroll);

            // Collect all toggle panels for count update
            var togglePanels = new List<(Panel pnl, Func<bool> check)>();
            int currentY = 0;

            // --- Card REDE ---
            var lblRede = new Label
            {
                Text = "REDE",
                Location = new Point(0, currentY),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            pnlScroll.Controls.Add(lblRede);
            currentY += 22;

            var items = new (string nome, string desc, Func<bool> isAtivo, Action ativar, Action desativar)[]
            {
                ("TcpNoDelay", "Envia pacotes imediatamente sem esperar", AntiDC.IsTcpNoDelayAtivo, AntiDC.AtivarTcpNoDelay, AntiDC.DesativarTcpNoDelay),
                ("Resposta TCP Rapida", "Confirma pacotes sem atraso", AntiDC.IsTcpAckFrequencyAtivo, AntiDC.AtivarTcpAckFrequency, AntiDC.DesativarTcpAckFrequency),
                ("KeepAlive Curto", "Checa conexao a cada 60s (padrao: 2h)", AntiDC.IsKeepAliveAtivo, AntiDC.AtivarKeepAlive, AntiDC.DesativarKeepAlive),
                ("Desativar IPv6", "WYD so usa IPv4 (fix oficial)", AntiDC.IsIPv6DesativadoAtivo, AntiDC.AtivarIPv6Desativado, AntiDC.DesativarIPv6Desativado),
                ("Rede Sempre Ligada", "Impede Windows de desligar o adaptador", AntiDC.IsNicPowerMgmtAtivo, AntiDC.AtivarNicPowerMgmt, AntiDC.DesativarNicPowerMgmt),
                ("Wi-Fi Maximo", "Desativa economia de energia do Wi-Fi", AntiDC.IsWifiPowerSaveAtivo, AntiDC.AtivarWifiPowerSave, AntiDC.DesativarWifiPowerSave),
                ("Liberar no Firewall", "Adiciona WYD como excecao no Firewall", AntiDC.IsFirewallWhitelistAtivo, AntiDC.AtivarFirewallWhitelist, AntiDC.DesativarFirewallWhitelist),
            };

            foreach (var (nome, desc, isAtivo, ativar, desativar) in items)
            {
                var p = CriarItemAntiDC(pnlScroll, currentY, nome, desc, isAtivo, ativar, desativar);
                togglePanels.Add((p, isAtivo));
                currentY += 56;
            }

            currentY += 10;

            // --- Card PROCESSO ---
            var lblProc = new Label
            {
                Text = "PROCESSO",
                Location = new Point(0, currentY),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            pnlScroll.Controls.Add(lblProc);
            currentY += 22;

            var procItems = new (string nome, string desc, Func<bool> isAtivo, Action ativar, Action desativar)[]
            {
                ("Prioridade Alta", "WYD roda antes de outros programas", AntiDC.IsHighPriorityAtivo, AntiDC.AtivarHighPriority, AntiDC.DesativarHighPriority),
                ("CPU Otimizada", "Fixa nos nucleos mais rapidos", AntiDC.IsCpuAffinityAtivo, AntiDC.AtivarCpuAffinity, AntiDC.DesativarCpuAffinity),
                ("Modo Performance", "Plano de energia maximo do Windows", AntiDC.IsHighPerfPlanAtivo, AntiDC.AtivarHighPerfPlan, AntiDC.DesativarHighPerfPlan),
            };

            foreach (var (nome, desc, isAtivo, ativar, desativar) in procItems)
            {
                var p = CriarItemAntiDC(pnlScroll, currentY, nome, desc, isAtivo, ativar, desativar);
                togglePanels.Add((p, isAtivo));
                currentY += 56;
            }

            // Update count helper
            void AtualizarContagem()
            {
                int count = 0;
                foreach (var (_, check) in togglePanels)
                {
                    try { if (check()) count++; } catch { }
                }
                lblOptCount.Text = $"{count} de 10 otimizacoes ativas";
                lblOptCount.ForeColor = count == 0 ? TEXT_DIM : count >= 7 ? ACCENT_GREEN : ACCENT_YELLOW;
            }

            antiDCRefreshCount = AtualizarContagem;
            AtualizarContagem();

            // Otimizar Tudo
            btnOtimizarTudo.Click += (s, e) =>
            {
                AntiDC.AtivarTcpNoDelay();
                AntiDC.AtivarTcpAckFrequency();
                AntiDC.AtivarKeepAlive();
                AntiDC.AtivarIPv6Desativado();
                AntiDC.AtivarNicPowerMgmt();
                AntiDC.AtivarWifiPowerSave();
                AntiDC.AtivarFirewallWhitelist();
                AntiDC.AtivarHighPriority();
                AntiDC.AtivarCpuAffinity();
                AntiDC.AtivarHighPerfPlan();
                foreach (var (pnl, check) in togglePanels) pnl.Invalidate(true);
                AtualizarContagem();
                AtualizarStatus("Todas as otimizacoes aplicadas!", ACCENT_GREEN);
            };

            // Reverter Tudo
            btnReverterTudo.Click += (s, e) =>
            {
                AntiDC.DesativarTcpNoDelay();
                AntiDC.DesativarTcpAckFrequency();
                AntiDC.DesativarKeepAlive();
                AntiDC.DesativarIPv6Desativado();
                AntiDC.DesativarNicPowerMgmt();
                AntiDC.DesativarWifiPowerSave();
                AntiDC.DesativarFirewallWhitelist();
                AntiDC.DesativarHighPriority();
                AntiDC.DesativarCpuAffinity();
                AntiDC.DesativarHighPerfPlan();
                foreach (var (pnl, check) in togglePanels) pnl.Invalidate(true);
                AtualizarContagem();
                AtualizarStatus("Todas as otimizacoes revertidas.", TEXT_DIM);
            };

            // --- Card MONITORAMENTO ---
            currentY += 10;
            var lblMonit = new Label
            {
                Text = "MONITORAMENTO",
                Location = new Point(0, currentY),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8, FontStyle.Bold)
            };
            pnlScroll.Controls.Add(lblMonit);
            currentY += 22;

            var cardMonit = new CardPanel
            {
                Location = new Point(0, currentY),
                Size = new Size(560, 140),
                CardColor = BG_CARD
            };
            pnlScroll.Controls.Add(cardMonit);

            lblTempoOnline = new Label
            {
                Text = "Tempo online: 00:00:00",
                Location = new Point(16, 12),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.Transparent
            };
            cardMonit.Controls.Add(lblTempoOnline);

            lblDcCount = new Label
            {
                Text = "Desconexoes: 0",
                Location = new Point(250, 12),
                AutoSize = true,
                ForeColor = ACCENT_GREEN,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.Transparent
            };
            cardMonit.Controls.Add(lblDcCount);

            lblSpikeCount = new Label
            {
                Text = "Picos de latencia: 0",
                Location = new Point(16, 36),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.Transparent
            };
            cardMonit.Controls.Add(lblSpikeCount);

            lblPingMedio = new Label
            {
                Text = "Ping medio: 0ms",
                Location = new Point(250, 36),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 9),
                BackColor = Color.Transparent
            };
            cardMonit.Controls.Add(lblPingMedio);

            var btnRelatorio = new ModernButton
            {
                Text = "Gerar Relatorio",
                Location = new Point(16, 68),
                Size = new Size(170, 32),
                BaseColor = Color.FromArgb(30, 40, 80),
                HoverColor = Color.FromArgb(40, 50, 100),
                AccentColor = ACCENT_BLUE,
                Radius = 6,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            cardMonit.Controls.Add(btnRelatorio);

            btnRelatorio.Click += (s, e) =>
            {
                string relatorio = AntiDC.GerarRelatorio();
                var dlg = new Form
                {
                    Text = "Relatorio Anti-DC",
                    Size = new Size(520, 440),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    BackColor = BG_DARK,
                    ForeColor = TEXT_PRIMARY
                };
                var txt = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Text = relatorio,
                    Location = new Point(12, 12),
                    Size = new Size(490, 340),
                    BackColor = BG_INPUT,
                    ForeColor = TEXT_PRIMARY,
                    Font = new Font("Consolas", 9),
                    BorderStyle = BorderStyle.None
                };
                dlg.Controls.Add(txt);
                var btnCopiar = new ModernButton
                {
                    Text = "Copiar",
                    Location = new Point(12, 362),
                    Size = new Size(120, 32),
                    BaseColor = Color.FromArgb(30, 40, 80),
                    HoverColor = Color.FromArgb(40, 50, 100),
                    AccentColor = ACCENT_BLUE,
                    Radius = 6,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold)
                };
                dlg.Controls.Add(btnCopiar);
                btnCopiar.Click += (s2, e2) =>
                {
                    Clipboard.SetText(relatorio);
                    btnCopiar.Text = "Copiado!";
                };
                dlg.ShowDialog(this);
            };

            var btnAbrirLogs = new ModernButton
            {
                Text = "Abrir Pasta de Logs",
                Location = new Point(196, 68),
                Size = new Size(150, 32),
                BaseColor = BG_INPUT,
                HoverColor = Color.FromArgb(50, 52, 62),
                AccentColor = TEXT_SECONDARY,
                Radius = 6,
                Font = new Font("Segoe UI", 9)
            };
            cardMonit.Controls.Add(btnAbrirLogs);

            btnAbrirLogs.Click += (s, e) =>
            {
                string dir = AntiDC.CaminhoLogDir;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            };

            currentY += 150;

            // Ping timer
            pingTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            pingTimer.Tick += async (s, e) =>
            {
                int ms = await AntiDC.PingAsync();

                // Update monitoring stats (always, even if tab not visible)
                AntiDC.RegistrarPing(ms);

                // Vigia: reaplica prioridade/afinidade em WYD que abriram depois (roda sempre).
                // Uma unica enumeracao de processos ja devolve os status usados na UI abaixo.
                var (wydTotal, prioAplicados, cpuAplicados) = AntiDC.ReaplicarEObterStatus();

                // Relatorio de DC pro painel admin: 1 envio ~60s apos abrir e depois a cada ~5min.
                dcReportTicks++;
                if (dcReportTicks % 100 == 20)
                    _ = Backend.EnviarDcReportAsync(AntiDC.DcCount, AntiDC.SpikeCount, AntiDC.PingMedio(), wydTotal);

                // Update UI only when visible
                if (pnlAntiDC.Visible)
                {
                    if (lblPingValue != null)
                    {
                        if (ms < 0) { lblPingValue.Text = "Sem resposta"; lblPingValue.ForeColor = ACCENT_RED; }
                        else if (ms < 80) { lblPingValue.Text = $"{ms}ms - Otimo"; lblPingValue.ForeColor = ACCENT_GREEN; }
                        else if (ms < 200) { lblPingValue.Text = $"{ms}ms - Ok"; lblPingValue.ForeColor = ACCENT_YELLOW; }
                        else { lblPingValue.Text = $"{ms}ms - Ruim"; lblPingValue.ForeColor = ACCENT_RED; }
                    }

                    // Update monitoring labels
                    lblTempoOnline.Text = $"Tempo online: {AntiDC.TempoOnlineStr()}";
                    lblDcCount.Text = $"Desconexoes: {AntiDC.DcCount}";
                    lblDcCount.ForeColor = AntiDC.DcCount > 0 ? ACCENT_RED : ACCENT_GREEN;
                    lblSpikeCount.Text = $"Picos de latencia: {AntiDC.SpikeCount}";
                    lblPingMedio.Text = $"Ping medio: {AntiDC.PingMedio()}ms";

                    // Indicador de WYD: quantos abertos e quantos com prioridade/afinidade aplicada
                    if (wydTotal == 0)
                    {
                        lblWydStatus.Text = "WYD: nenhum aberto";
                        lblWydStatus.ForeColor = TEXT_DIM;
                    }
                    else
                    {
                        lblWydStatus.Text = $"WYD: {wydTotal} | Prio {prioAplicados}/{wydTotal} | CPU {cpuAplicados}/{wydTotal}";
                        bool tudoOk = (!AntiDC.HighPriorityDesejado || prioAplicados == wydTotal)
                                   && (!AntiDC.CpuAffinityDesejado || cpuAplicados == wydTotal);
                        lblWydStatus.ForeColor = tudoOk ? ACCENT_GREEN : ACCENT_YELLOW;
                    }
                }
            };
            pingTimer.Start();
        }

        private Action? antiDCRefreshCount;

        private Panel CriarItemAntiDC(Control parent, int y, string nome, string descricao, Func<bool> isAtivo, Action ativar, Action desativar)
        {
            var pnlItem = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(560, 52),
                BackColor = BG_CARD,
                Cursor = Cursors.Hand
            };

            // Custom checkbox indicator
            var pnlCheck = new Panel
            {
                Location = new Point(14, 16),
                Size = new Size(20, 20),
                BackColor = Color.Transparent
            };
            pnlCheck.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, 18, 18);
                bool ativo = false;
                try { ativo = isAtivo(); } catch { }
                if (ativo)
                {
                    using var fill = new SolidBrush(ACCENT_GREEN);
                    using var path = Gfx.RoundedRect(rect, 4);
                    e.Graphics.FillPath(fill, path);
                    using var pen = new Pen(Color.FromArgb(10, 10, 10), 2.2f);
                    e.Graphics.DrawLine(pen, 4, 9, 8, 14);
                    e.Graphics.DrawLine(pen, 8, 14, 15, 4);
                }
                else
                {
                    using var pen = new Pen(Color.FromArgb(80, 80, 90), 1.5f);
                    using var path = Gfx.RoundedRect(rect, 4);
                    e.Graphics.DrawPath(pen, path);
                }
            };
            pnlItem.Controls.Add(pnlCheck);

            var lblNome = new Label
            {
                Text = nome,
                Location = new Point(44, 8),
                AutoSize = true,
                ForeColor = TEXT_PRIMARY,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            pnlItem.Controls.Add(lblNome);

            var lblDesc = new Label
            {
                Text = descricao,
                Location = new Point(44, 28),
                AutoSize = true,
                ForeColor = TEXT_SECONDARY,
                Font = new Font("Segoe UI", 8),
                BackColor = Color.Transparent
            };
            pnlItem.Controls.Add(lblDesc);

            // Click handler - toggle
            void OnClick(object? s, EventArgs e)
            {
                try
                {
                    if (isAtivo()) desativar(); else ativar();
                }
                catch { }
                pnlCheck.Invalidate();
                antiDCRefreshCount?.Invoke();
            }

            pnlItem.Click += OnClick;
            lblNome.Click += OnClick;
            lblDesc.Click += OnClick;
            pnlCheck.Click += OnClick;

            parent.Controls.Add(pnlItem);
            return pnlItem;
        }

        // Fontes reutilizadas no desenho da lista (evita alocar por item a cada redesenho)
        private static readonly Font FonteListaMacros = new("Segoe UI", 9.5f);
        private static readonly Font FonteBadge = new("Segoe UI", 8f, FontStyle.Bold);

        // Desenho customizado da ListBox: bolinha de status + nome + badge do atalho
        private void LstMacros_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selected ? Color.FromArgb(46, ACCENT_GOLD.R, ACCENT_GOLD.G, ACCENT_GOLD.B) : BG_INPUT;
            Color fg = selected ? ACCENT_GOLD : TEXT_PRIMARY;

            using (var bgBrush = new SolidBrush(bg))
                g.FillRectangle(bgBrush, e.Bounds);

            if (selected)
                using (var accentPen = new Pen(ACCENT_GOLD, 2))
                    g.DrawLine(accentPen, e.Bounds.X, e.Bounds.Y, e.Bounds.X, e.Bounds.Bottom);

            Macro? macro = e.Index < biblioteca.Macros.Count ? biblioteca.Macros[e.Index] : null;
            int cy = e.Bounds.Y + e.Bounds.Height / 2;

            // Bolinha de status: verde = ja tem acoes gravadas; cinza = vazio (falta gravar)
            bool temAcoes = macro != null && macro.Eventos.Count > 0;
            using (var dotB = new SolidBrush(temAcoes ? ACCENT_GREEN : Color.FromArgb(96, 98, 108)))
                g.FillEllipse(dotB, e.Bounds.X + 12, cy - 4, 8, 8);

            // Nome do macro (centralizado na vertical)
            string nome = macro?.Name ?? (lstMacros.Items[e.Index].ToString() ?? "");
            using (var textBrush = new SolidBrush(fg))
            using (var sfV = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(nome, FonteListaMacros, textBrush,
                    new RectangleF(e.Bounds.X + 28, e.Bounds.Y, e.Bounds.Width - 90, e.Bounds.Height), sfV);

            // Badge do atalho (F5, F6...) a direita
            if (macro != null && !string.IsNullOrEmpty(macro.Hotkey))
            {
                var sz = g.MeasureString(macro.Hotkey, FonteBadge);
                int bw = (int)sz.Width + 12, bh = 17;
                int bxr = e.Bounds.Right - bw - 10, byr = cy - bh / 2;
                using (var badgeBg = new SolidBrush(Color.FromArgb(64, ACCENT_GOLD.R, ACCENT_GOLD.G, ACCENT_GOLD.B)))
                using (var path = Gfx.RoundedRect(new Rectangle(bxr, byr, bw, bh), 5))
                    g.FillPath(badgeBg, path);
                using (var badgeTx = new SolidBrush(ACCENT_GOLD))
                using (var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(macro.Hotkey, FonteBadge, badgeTx, new RectangleF(bxr, byr, bw, bh), sfC);
            }
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
            // Descobre o login.mp3 pela pasta do WYD em execucao (robusto a instalacao fora do padrao).
            string musica = AntiDC.MusicaWydDetectada();
            if (!string.IsNullOrEmpty(musica))
            {
                MciPlayer.Abrir(musica);
                MciPlayer.SetVolume(Math.Clamp(biblioteca.Config.VolumeMusica, 0, 1000));
                MciPlayer.Tocar(loop: true);
            }
        }

        private void MostrarAba(string aba)
        {
            pnlMacros.Visible = aba == "macros";
            pnlTutorial.Visible = aba == "tutorial";
            pnlConfig.Visible = aba == "config";
            pnlAntiDC.Visible = aba == "antidc";

            foreach (var (btn, id) in new[] { (btnTabMacros, "macros"), (btnTabTutorial, "tutorial"), (btnTabConfig, "config"), (btnTabAntiDC, "antidc") })
            {
                bool ativo = aba == id;
                // Aba ativa = "text tab" (funde com a barra) + texto dourado + underline; inativa = botao sutil
                btn.BaseColor = ativo ? Color.FromArgb(22, 24, 30) : Color.FromArgb(45, 47, 55);
                btn.ForeColor = ativo ? ACCENT_GOLD : TEXT_SECONDARY;
                btn.Invalidate();
            }
            abaAtiva = aba;
            pnlTabs.Invalidate(); // redesenha o underline dourado
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

        private System.Windows.Forms.Timer? saveDebounceTimer;
        private bool salvamentoPendente;

        // Salvamento adiado: arrastar um NumericUpDown/campo dispara varios eventos seguidos.
        // Em vez de serializar e escrever em disco a cada tique, junta tudo numa unica escrita ~600ms
        // depois da ultima alteracao. Flush garantido ao fechar o app.
        private void SalvarBibliotecaDebounced()
        {
            salvamentoPendente = true;
            if (saveDebounceTimer == null)
            {
                saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 600 };
                saveDebounceTimer.Tick += (s, e) =>
                {
                    saveDebounceTimer!.Stop();
                    FlushSalvamentoPendente();
                };
            }
            saveDebounceTimer.Stop();
            saveDebounceTimer.Start();
        }

        private void FlushSalvamentoPendente()
        {
            saveDebounceTimer?.Stop();
            if (salvamentoPendente)
            {
                salvamentoPendente = false;
                SalvarBiblioteca();
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

            // Clamp defensivo: JSON adulterado/antigo nao pode estourar ArgumentOutOfRangeException
            nudRepeticoes.Value = Math.Clamp(macroSelecionado.Repeticoes, (int)nudRepeticoes.Minimum, (int)nudRepeticoes.Maximum);
            nudIntervalo.Value = Math.Clamp(macroSelecionado.IntervaloMs, (int)nudIntervalo.Minimum, (int)nudIntervalo.Maximum);
            nudAtraso.Value = Math.Clamp(macroSelecionado.AtrasoInicialMs, (int)nudAtraso.Minimum, (int)nudAtraso.Maximum);
            lblAcoes.Text = $"{macroSelecionado.Eventos.Count} acoes gravadas";
            lblParamTitulo.Text = "MACRO: " + macroSelecionado.Name.ToUpperInvariant();

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
            SalvarBibliotecaDebounced();
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
            IniciarContagemEGravar();
        }

        private void IniciarGravacao()
        {
            gravando = true;
            eventosGravados = new List<MacroEvent>();
            ultimoMoveT = -1;
            gravacaoStopwatch = Stopwatch.StartNew();
            AtualizarStatus("GRAVANDO...  (ESC para parar)", ACCENT_RED);
            SinalRec(true);

            IntPtr hMod = Win32.GetModuleHandle(null!);
            mouseHookProc = MouseHookCallback;
            mouseHookId = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, mouseHookProc, hMod, 0);
            keyboardHookProc = KeyboardHookCallback;
            keyboardHookId = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, keyboardHookProc, hMod, 0);

            // Se algum hook nao instalou, a gravacao ficaria "morta" em silencio: reverte e avisa.
            if (mouseHookId == IntPtr.Zero || keyboardHookId == IntPtr.Zero)
            {
                if (mouseHookId != IntPtr.Zero) { Win32.UnhookWindowsHookEx(mouseHookId); mouseHookId = IntPtr.Zero; }
                if (keyboardHookId != IntPtr.Zero) { Win32.UnhookWindowsHookEx(keyboardHookId); keyboardHookId = IntPtr.Zero; }
                gravando = false;
                gravacaoStopwatch?.Stop();
                SinalRec(false);
                btnGravar.Enabled = true;
                btnPararGravacao.Enabled = false;
                AtualizarStatus("Erro ao iniciar a gravacao (hook negado). Rode como admin e tente de novo.", ACCENT_RED);
            }
        }

        private void PararGravacao()
        {
            if (!gravando) return;
            gravando = false;
            SinalRec(false);

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
            sinalParar.Reset();

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
            sinalParar.Set(); // acorda a espera imediatamente (botao de panico responde na hora)
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
            if (ms <= 0 || !reproduzindo) return;
            // Espera exata e cancelavel: acorda no tempo certo ou na hora em que a reproducao e parada.
            sinalParar.Wait(ms);
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

            var hotkeyUsada = new HashSet<string>();
            // Marcar hotkeys do sistema como usadas
            hotkeyUsada.Add(biblioteca.Config.HotkeyGravar);
            hotkeyUsada.Add(biblioteca.Config.HotkeyPanico);

            for (int i = 0; i < biblioteca.Macros.Count; i++)
            {
                var macro = biblioteca.Macros[i];
                if (string.IsNullOrEmpty(macro.Hotkey)) continue;
                if (hotkeyUsada.Contains(macro.Hotkey))
                {
                    AtualizarStatus($"Atalho {macro.Hotkey} duplicado em \"{macro.Name}\" (ignorado)", ACCENT_YELLOW);
                    continue;
                }
                uint vk = HotkeyParaVK(macro.Hotkey);
                if (vk == 0) continue;
                hotkeyUsada.Add(macro.Hotkey);
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
                        IniciarContagemEGravar();
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Onboarding: so na primeira vez que o app abre
            if (!biblioteca.Config.JaViuBoasVindas)
            {
                try { using var f = new BoasVindasForm(); f.ShowDialog(this); } catch { }
                biblioteca.Config.JaViuBoasVindas = true;
                SalvarBiblioteca();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (reproduzindo) PararReproducao();
            if (gravando) PararGravacao();
            foreach (var id in hotkeysRegistrados.Keys) Win32.UnregisterHotKey(Handle, id);
            Win32.UnregisterHotKey(Handle, HOTKEY_PANICO_ID);
            Win32.UnregisterHotKey(Handle, HOTKEY_GRAVAR_ID);
            pingTimer?.Stop(); pingTimer?.Dispose();
            // Desativar proxy se ficou ligado, senao internet do usuario para
            try { if (ProxyHack.IsAtivo()) ProxyHack.Desativar(); } catch { }
            AntiDC.FinalizarSessao();
            MciPlayer.Fechar();
            brasaoImg?.Dispose();
            saveDebounceTimer?.Stop(); saveDebounceTimer?.Dispose();
            salvamentoPendente = false; // o SalvarBiblioteca abaixo ja persiste o estado atual
            sinalParar.Dispose();
            SalvarBiblioteca();
            base.OnFormClosing(e);
        }
    }
}
