using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MacroSupremes
{
    // ======================================================================
    // LICENCA (telefone + senha, validada no backend Cloudflare)
    // ======================================================================
    // Modo SUAVE por padrao: a tela aparece e onboarda, mas da pra pular e usar mesmo assim.
    // Quando o dono decidir cobrar, basta ligar BloquearSemLicenca = true (trava dura).
    // Falha de rede = fail-open (o app abre offline; revogacao so vale quando ha internet).
    internal static class Licenca
    {
        // Ligar isto (true) faz o app NAO abrir sem licenca valida. Deixar false = modo suave.
        public const bool BloquearSemLicenca = false;

        private static string LicPath => Path.Combine(Canal.DirDados, "licenca.bin");

        private sealed class Cred { public string Phone { get; set; } = ""; public string Senha { get; set; } = ""; }

        // --- Armazenamento local cifrado (DPAPI, atrelado ao usuario do Windows) ---
        private static void SalvarLocal(string phone, string senha)
        {
            try
            {
                Directory.CreateDirectory(Canal.DirDados);
                var json = JsonSerializer.Serialize(new Cred { Phone = phone, Senha = senha });
                var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(LicPath, enc);
            }
            catch { }
        }

        private static Cred? CarregarLocal()
        {
            try
            {
                if (!File.Exists(LicPath)) return null;
                var dec = ProtectedData.Unprotect(File.ReadAllBytes(LicPath), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Cred>(Encoding.UTF8.GetString(dec));
            }
            catch { return null; }
        }

        private static void LimparLocal()
        {
            try { if (File.Exists(LicPath)) File.Delete(LicPath); } catch { }
        }

        // Chamado no boot (antes da UI). Retorna false SO no modo duro sem licenca valida.
        public static bool GarantirNoBoot()
        {
            var cred = CarregarLocal();
            if (cred != null && !string.IsNullOrEmpty(cred.Phone))
            {
                var (ok, reason) = Backend.PostLicencaAsync("/license/validate", cred.Phone, cred.Senha)
                    .GetAwaiter().GetResult();
                if (ok) return true;
                if (reason == "sem_conexao") return true; // offline: nao trava
                // credencial invalida (revogado/outra maquina/senha): apaga e pede login de novo
                LimparLocal();
            }

            using var form = new LoginForm();
            var res = form.ShowDialog();
            if (res == DialogResult.OK)
            {
                SalvarLocal(form.PhoneOk, form.SenhaOk);
                return true;
            }
            // pulou / fechou
            return !BloquearSemLicenca;
        }
    }

    // ======================================================================
    // TELA DE LOGIN / CADASTRO
    // ======================================================================
    internal sealed class LoginForm : Form
    {
        public string PhoneOk { get; private set; } = "";
        public string SenhaOk { get; private set; } = "";

        private static readonly Color BG = Color.FromArgb(24, 26, 32);
        private static readonly Color CARD = Color.FromArgb(38, 40, 48);
        private static readonly Color INPUT = Color.FromArgb(30, 32, 40);
        private static readonly Color GREEN = Color.FromArgb(76, 217, 100);
        private static readonly Color TXT = Color.FromArgb(230, 232, 238);
        private static readonly Color DIM = Color.FromArgb(150, 152, 160);

        private readonly TextBox txtPhone;
        private readonly TextBox txtSenha;
        private readonly Label lblMsg;
        private readonly Button btnEntrar;
        private readonly Button btnCriar;
        private readonly Button btnPular;

        public LoginForm()
        {
            Text = "Acesso - Macro Supremes" + Canal.SufixoTitulo;
            ClientSize = new Size(380, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG;
            ForeColor = TXT;
            Font = new Font("Segoe UI", 9.5f);

            var lblTitulo = new Label
            {
                Text = "Entrar na guilda",
                Font = new Font("Segoe UI", 15, FontStyle.Bold),
                ForeColor = GREEN,
                AutoSize = true,
                Location = new Point(24, 22),
            };
            var lblSub = new Label
            {
                Text = "Use o telefone do WhatsApp da guilda e crie uma senha.",
                ForeColor = DIM,
                AutoSize = true,
                Location = new Point(24, 54),
            };

            var lblPhone = new Label { Text = "Telefone (com DDD)", ForeColor = DIM, AutoSize = true, Location = new Point(24, 88) };
            txtPhone = NovoInput(24, 108);
            txtPhone.PlaceholderText = "Ex: 19 99999-8888";

            var lblSenha = new Label { Text = "Senha", ForeColor = DIM, AutoSize = true, Location = new Point(24, 146) };
            txtSenha = NovoInput(24, 166);
            txtSenha.UseSystemPasswordChar = true;

            lblMsg = new Label { Text = "", ForeColor = Color.FromArgb(240, 120, 120), AutoSize = false, Size = new Size(332, 20), Location = new Point(24, 204) };

            btnEntrar = NovoBotao("Entrar", 24, 230, 150, GREEN, Color.FromArgb(10, 10, 10));
            btnEntrar.Click += async (s, e) => await Acao("/license/validate");

            btnCriar = NovoBotao("Criar conta", 182, 230, 150, Color.FromArgb(55, 58, 68), TXT);
            btnCriar.Click += async (s, e) => await Acao("/license/register");

            btnPular = NovoBotao(Licenca.BloquearSemLicenca ? "Sair" : "Pular por agora", 24, 278, 308, Color.FromArgb(40, 42, 50), DIM);
            btnPular.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { lblTitulo, lblSub, lblPhone, txtPhone, lblSenha, txtSenha, lblMsg, btnEntrar, btnCriar, btnPular });
            AcceptButton = btnEntrar;
        }

        private TextBox NovoInput(int x, int y)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(332, 26),
                BackColor = INPUT,
                ForeColor = TXT,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10),
            };
        }

        private Button NovoBotao(string txt, int x, int y, int w, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = txt,
                Location = new Point(x, y),
                Size = new Size(w, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = fg,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private async Task Acao(string rota)
        {
            string phone = new string(txtPhone.Text.ToCharArray()); // trim leve
            string senha = txtSenha.Text;
            if (phone.Replace(" ", "").Replace("-", "").Length < 8) { Msg("Telefone invalido."); return; }
            if (senha.Length < 4) { Msg("A senha precisa de ao menos 4 caracteres."); return; }

            Habilitar(false);
            Msg("Conectando...");
            var (ok, reason) = await Backend.PostLicencaAsync(rota, phone, senha);
            Habilitar(true);

            if (ok)
            {
                PhoneOk = phone;
                SenhaOk = senha;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            Msg(Explicar(reason, rota));
        }

        private static string Explicar(string reason, string rota) => reason switch
        {
            "sem_conexao" => "Sem conexao com o servidor. Tente de novo ou pule por agora.",
            "ja_cadastrado" => "Esse telefone ja tem conta. Use \"Entrar\".",
            "nao_cadastrado" => "Telefone nao cadastrado. Use \"Criar conta\".",
            "senha_errada" => "Senha incorreta.",
            "revogado" => "Sua conta foi bloqueada. Fale com o admin da guilda.",
            "outra_maquina" => "Essa conta ja esta amarrada a outro PC. Fale com o admin pra liberar.",
            "senha_curta" => "A senha precisa de ao menos 4 caracteres.",
            "phone_invalido" => "Telefone invalido.",
            _ => rota.Contains("register") ? "Nao deu pra criar a conta agora." : "Nao deu pra entrar agora.",
        };

        private void Msg(string m) { lblMsg.ForeColor = m == "Conectando..." ? DIM : Color.FromArgb(240, 120, 120); lblMsg.Text = m; }
        private void Habilitar(bool on) { btnEntrar.Enabled = on; btnCriar.Enabled = on; txtPhone.Enabled = on; txtSenha.Enabled = on; }
    }

    // ======================================================================
    // BOAS-VINDAS (primeira vez): explica em 3 passos como criar um macro
    // ======================================================================
    internal sealed class BoasVindasForm : Form
    {
        private static readonly Color BG = Color.FromArgb(24, 26, 32);
        private static readonly Color GOLD = Color.FromArgb(212, 175, 55);
        private static readonly Color TXT = Color.FromArgb(230, 232, 238);
        private static readonly Color DIM = Color.FromArgb(160, 162, 172);

        public BoasVindasForm()
        {
            Text = "Bem-vindo" + Canal.SufixoTitulo;
            ClientSize = new Size(452, 356);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = BG;
            ForeColor = TXT;
            Font = new Font("Segoe UI", 9.5f);

            var titulo = new Label
            {
                Text = "Bem-vindo à Guilda Supremus!",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = GOLD,
                AutoSize = true,
                Location = new Point(24, 22),
            };
            var sub = new Label
            {
                Text = "Criar seu primeiro macro leva 3 passos:",
                ForeColor = DIM,
                AutoSize = true,
                Location = new Point(24, 58),
            };
            var passos = new Label
            {
                Text =
                    "①   Escolha um macro na lista à esquerda (ex: Auto Pergaminho).\n\n" +
                    "②   Clique em Gravar, espere o 3·2·1 e faça no jogo o que\n" +
                    "      quer repetir. Aperte ESC para parar.\n\n" +
                    "③   Use Testar pra conferir e Salvar. Depois é só apertar a\n" +
                    "      tecla de atalho com o jogo aberto.",
                ForeColor = TXT,
                Location = new Point(24, 92),
                Size = new Size(404, 200),
                Font = new Font("Segoe UI", 10.5f),
            };

            var btn = new Button
            {
                Text = "Começar",
                Location = new Point(24, 306),
                Size = new Size(404, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = GOLD,
                ForeColor = Color.FromArgb(10, 10, 10),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            Controls.AddRange(new Control[] { titulo, sub, passos, btn });
            AcceptButton = btn;
        }
    }
}
