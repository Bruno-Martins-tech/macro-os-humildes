// ============================================================================
// Macro Supremes - Worker Cloudflare (free tier)
// Rotas:
//   POST /heartbeat          telemetria de uso (anonima): base instalada + DAU + versoes
//   POST /license/register   cria conta (telefone + senha), amarra na maquina
//   POST /license/validate   valida telefone+senha+maquina a cada abertura do app
//   GET  /admin/stats        painel simples (ativos/dia, versoes) [Authorization: Bearer ADMIN_TOKEN]
//   GET  /admin/accounts     lista contas [admin]
//   POST /admin/revoke       revoga/reativa uma conta [admin]
//   POST /admin/reset-machine  desamarra a maquina (pessoa trocou de PC) [admin]
// Cron (quinta): checa a pagina de updates do WYD e avisa no Discord se mudou.
// ============================================================================

const json = (obj, status = 200) =>
  new Response(JSON.stringify(obj), { status, headers: { 'content-type': 'application/json' } });

const nowIso = () => new Date().toISOString();
const today = () => nowIso().slice(0, 10); // YYYY-MM-DD (UTC)

// So digitos; corta DDI/zeros a esquerda de forma leve (mantem o numero cheio).
function normPhone(p) {
  return String(p || '').replace(/\D+/g, '');
}

// --- Hash de senha: PBKDF2-SHA256 (SubtleCrypto, disponivel no Worker) ---
function toHex(buf) {
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
function randSaltHex() {
  const a = new Uint8Array(16);
  crypto.getRandomValues(a);
  return toHex(a.buffer);
}
async function hashPassword(senha, saltHex) {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey('raw', enc.encode(senha), 'PBKDF2', false, ['deriveBits']);
  const salt = Uint8Array.from(saltHex.match(/.{2}/g).map((h) => parseInt(h, 16)));
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', salt, iterations: 100000, hash: 'SHA-256' },
    key,
    256
  );
  return toHex(bits);
}
// Comparacao em tempo ~constante
function safeEqual(a, b) {
  if (a.length !== b.length) return false;
  let r = 0;
  for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return r === 0;
}

async function readJson(request) {
  try { return await request.json(); } catch { return {}; }
}

// ---------------------------------------------------------------------------
// TELEMETRIA
// ---------------------------------------------------------------------------
async function handleHeartbeat(request, env) {
  const b = await readJson(request);
  const machine = String(b.machine || '').slice(0, 128);
  if (!machine) return json({ ok: false, error: 'machine ausente' }, 400);
  const version = String(b.version || '').slice(0, 32);
  const channel = b.channel === 'staging' ? 'staging' : 'stable';
  const ts = nowIso();

  // base instalada (upsert com contador)
  await env.DB.prepare(
    `INSERT INTO devices (machine, version, channel, first_seen, last_seen, seen_count)
     VALUES (?1, ?2, ?3, ?4, ?4, 1)
     ON CONFLICT(machine) DO UPDATE SET
       version = ?2, channel = ?3, last_seen = ?4, seen_count = seen_count + 1`
  ).bind(machine, version, channel, ts).run();

  // DAU: 1 por maquina por dia
  await env.DB.prepare(
    `INSERT INTO heartbeats (day, machine, version, channel) VALUES (?1, ?2, ?3, ?4)
     ON CONFLICT(day, machine) DO UPDATE SET version = ?3, channel = ?4`
  ).bind(today(), machine, version, channel).run();

  return json({ ok: true });
}

// ---------------------------------------------------------------------------
// LICENCA
// ---------------------------------------------------------------------------
async function handleRegister(request, env) {
  const b = await readJson(request);
  const phone = normPhone(b.phone);
  const senha = String(b.password || '');
  const machine = String(b.machine || '').slice(0, 128);
  if (phone.length < 8) return json({ ok: false, reason: 'phone_invalido' }, 400);
  if (senha.length < 4) return json({ ok: false, reason: 'senha_curta' }, 400);
  if (!machine) return json({ ok: false, reason: 'machine_ausente' }, 400);

  const existing = await env.DB.prepare('SELECT phone FROM accounts WHERE phone = ?1').bind(phone).first();
  if (existing) return json({ ok: false, reason: 'ja_cadastrado' }, 409);

  const salt = randSaltHex();
  const hash = await hashPassword(senha, salt);
  await env.DB.prepare(
    `INSERT INTO accounts (phone, pass_hash, pass_salt, machine, status, created_at, last_login)
     VALUES (?1, ?2, ?3, ?4, 'active', ?5, ?5)`
  ).bind(phone, hash, salt, machine, nowIso()).run();

  return json({ ok: true, status: 'active' });
}

async function handleValidate(request, env) {
  const b = await readJson(request);
  const phone = normPhone(b.phone);
  const senha = String(b.password || '');
  const machine = String(b.machine || '').slice(0, 128);
  if (!phone || !senha || !machine) return json({ ok: false, reason: 'dados_incompletos' }, 400);

  const acc = await env.DB.prepare('SELECT * FROM accounts WHERE phone = ?1').bind(phone).first();
  if (!acc) return json({ ok: false, reason: 'nao_cadastrado' }, 404);
  if (acc.status === 'revoked') return json({ ok: false, reason: 'revogado' }, 403);

  const hash = await hashPassword(senha, acc.pass_salt);
  if (!safeEqual(hash, acc.pass_hash)) return json({ ok: false, reason: 'senha_errada' }, 401);

  // Amarracao de maquina: 1 conta = 1 PC. Se ainda nao tem, amarra agora.
  if (!acc.machine) {
    await env.DB.prepare('UPDATE accounts SET machine = ?1 WHERE phone = ?2').bind(machine, phone).run();
  } else if (acc.machine !== machine) {
    return json({ ok: false, reason: 'outra_maquina' }, 403);
  }

  await env.DB.prepare('UPDATE accounts SET last_login = ?1 WHERE phone = ?2').bind(nowIso(), phone).run();
  return json({ ok: true, status: 'active' });
}

// ---------------------------------------------------------------------------
// ADMIN (moderacao) - protegido por Bearer ADMIN_TOKEN
// ---------------------------------------------------------------------------
function isAdmin(request, env) {
  const auth = request.headers.get('authorization') || '';
  const tok = auth.replace(/^Bearer\s+/i, '');
  return env.ADMIN_TOKEN && tok && safeEqual(tok, env.ADMIN_TOKEN);
}

async function handleAdminStats(env) {
  const dau = await env.DB.prepare(
    `SELECT day, COUNT(*) AS ativos FROM heartbeats
     WHERE day >= date('now', '-14 days') GROUP BY day ORDER BY day DESC`
  ).all();
  const versoes = await env.DB.prepare(
    `SELECT version, channel, COUNT(*) AS n FROM devices
     WHERE last_seen >= datetime('now', '-30 days')
     GROUP BY version, channel ORDER BY n DESC`
  ).all();
  const totais = await env.DB.prepare(
    `SELECT
       (SELECT COUNT(*) FROM devices) AS instalacoes,
       (SELECT COUNT(*) FROM accounts WHERE status='active') AS contas_ativas,
       (SELECT COUNT(*) FROM accounts WHERE status='revoked') AS contas_revogadas,
       (SELECT COUNT(*) FROM heartbeats WHERE day = date('now')) AS ativos_hoje`
  ).first();
  return json({ ok: true, totais, dau: dau.results, versoes: versoes.results });
}

async function handleAdminAccounts(env) {
  const r = await env.DB.prepare(
    `SELECT phone, status, nome, machine, created_at, last_login FROM accounts ORDER BY created_at DESC LIMIT 500`
  ).all();
  return json({ ok: true, accounts: r.results });
}

async function handleAdminRevoke(request, env) {
  const b = await readJson(request);
  const phone = normPhone(b.phone);
  const status = b.status === 'active' ? 'active' : 'revoked'; // default revoga
  if (!phone) return json({ ok: false, reason: 'phone_ausente' }, 400);
  await env.DB.prepare('UPDATE accounts SET status = ?1 WHERE phone = ?2').bind(status, phone).run();
  return json({ ok: true, phone, status });
}

async function handleAdminResetMachine(request, env) {
  const b = await readJson(request);
  const phone = normPhone(b.phone);
  if (!phone) return json({ ok: false, reason: 'phone_ausente' }, 400);
  await env.DB.prepare('UPDATE accounts SET machine = NULL WHERE phone = ?1').bind(phone).run();
  return json({ ok: true, phone, machine: null });
}

// ---------------------------------------------------------------------------
// CRON: vigia de patch do WYD -> Discord
// ---------------------------------------------------------------------------
async function checarPatchWyd(env) {
  if (!env.WYD_UPDATES_URL) return;
  let sig = '';
  try {
    const res = await fetch(env.WYD_UPDATES_URL, { headers: { 'User-Agent': 'MacroSupremes-Watch/1.0' } });
    const html = await res.text();
    // Assinatura estavel: hash do texto visivel (tira tags/scripts/espacos).
    const texto = html
      .replace(/<script[\s\S]*?<\/script>/gi, '')
      .replace(/<style[\s\S]*?<\/style>/gi, '')
      .replace(/<[^>]+>/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
    const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(texto));
    sig = toHex(buf);
  } catch (e) {
    return; // sem internet/pagina fora: nao faz nada
  }

  const prev = await env.DB.prepare('SELECT last_signature FROM wyd_patch_state WHERE id = 1').first();
  await env.DB.prepare(
    `INSERT INTO wyd_patch_state (id, last_signature, last_checked) VALUES (1, ?1, ?2)
     ON CONFLICT(id) DO UPDATE SET last_signature = ?1, last_checked = ?2`
  ).bind(sig, nowIso()).run();

  // 1a checagem (sem baseline) so grava, nao alerta.
  if (prev && prev.last_signature && prev.last_signature !== sig && env.DISCORD_WEBHOOK_URL) {
    await fetch(env.DISCORD_WEBHOOK_URL, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        content:
          '**Possivel patch do WYD detectado.** A pagina de updates mudou.\n' +
          env.WYD_UPDATES_URL +
          '\nVale testar o macro (posicoes gravadas, musica, Anti-DC) antes de liberar geral.',
      }),
    });
  }
}

// ---------------------------------------------------------------------------
// ROTEADOR
// ---------------------------------------------------------------------------
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const p = url.pathname;
    const m = request.method;

    try {
      if (p === '/' || p === '/health') return json({ ok: true, service: 'macro-supremes' });

      // Painel de admin (HTML). A pagina e publica; os dados exigem o ADMIN_TOKEN digitado nela.
      if (p === '/admin' && m === 'GET')
        return new Response(ADMIN_HTML, { headers: { 'content-type': 'text/html; charset=utf-8' } });

      if (p === '/heartbeat' && m === 'POST') return await handleHeartbeat(request, env);
      if (p === '/license/register' && m === 'POST') return await handleRegister(request, env);
      if (p === '/license/validate' && m === 'POST') return await handleValidate(request, env);

      if (p.startsWith('/admin/')) {
        if (!isAdmin(request, env)) return json({ ok: false, reason: 'nao_autorizado' }, 401);
        if (p === '/admin/stats' && m === 'GET') return await handleAdminStats(env);
        if (p === '/admin/accounts' && m === 'GET') return await handleAdminAccounts(env);
        if (p === '/admin/revoke' && m === 'POST') return await handleAdminRevoke(request, env);
        if (p === '/admin/reset-machine' && m === 'POST') return await handleAdminResetMachine(request, env);
      }

      return json({ ok: false, reason: 'nao_encontrado' }, 404);
    } catch (e) {
      return json({ ok: false, reason: 'erro_interno', detail: String(e && e.message || e) }, 500);
    }
  },

  async scheduled(event, env, ctx) {
    ctx.waitUntil(checarPatchWyd(env));
  },
};

// ---------------------------------------------------------------------------
// PAINEL DE ADMIN (HTML servido pelo Worker)
// ---------------------------------------------------------------------------
const ADMIN_HTML = `<!doctype html>
<html lang="pt-br"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Macro Supremes - Admin</title>
<style>
  :root{--bg:#16181e;--card:#262830;--in:#1e2028;--gr:#4cd964;--ye:#e2b23a;--rd:#e05252;--tx:#e6e8ee;--dim:#9698a0}
  *{box-sizing:border-box} body{margin:0;background:var(--bg);color:var(--tx);font:14px/1.4 Segoe UI,system-ui,sans-serif}
  header{padding:16px 20px;border-bottom:1px solid #2c2f38;display:flex;align-items:center;gap:12px}
  header h1{font-size:17px;margin:0;color:var(--gr)} header .sub{color:var(--dim);font-size:12px}
  .wrap{max-width:1000px;margin:0 auto;padding:20px}
  .row{display:flex;gap:10px;flex-wrap:wrap}
  input,button{font:14px Segoe UI,sans-serif;border-radius:6px;border:1px solid #333;padding:8px 12px}
  input{background:var(--in);color:var(--tx);min-width:280px}
  button{background:#3a3d48;color:var(--tx);cursor:pointer;border:0} button:hover{background:#474b58}
  button.p{background:var(--gr);color:#0a0a0a;font-weight:700}
  .cards{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}
  .c{background:var(--card);border-radius:10px;padding:14px 18px;min-width:130px}
  .c .n{font-size:26px;font-weight:700} .c .l{color:var(--dim);font-size:12px}
  table{width:100%;border-collapse:collapse;margin-top:10px;background:var(--card);border-radius:10px;overflow:hidden}
  th,td{text-align:left;padding:9px 12px;border-bottom:1px solid #2c2f38;font-size:13px}
  th{color:var(--dim);font-weight:600;font-size:11px;text-transform:uppercase}
  .tag{padding:2px 8px;border-radius:20px;font-size:11px;font-weight:700}
  .tag.a{background:rgba(76,217,100,.15);color:var(--gr)} .tag.r{background:rgba(224,82,82,.15);color:var(--rd)}
  .mut{color:var(--dim);font-family:Consolas,monospace;font-size:12px}
  .act button{padding:5px 9px;font-size:12px;margin-right:6px}
  h2{font-size:13px;color:var(--dim);text-transform:uppercase;margin:24px 0 4px}
  #msg{color:var(--ye);min-height:18px;margin-top:8px}
</style></head>
<body>
<header><h1>Macro Supremes</h1><span class="sub">Painel de Admin</span></header>
<div class="wrap">
  <div class="row">
    <input id="tk" type="password" placeholder="Cole o ADMIN_TOKEN aqui" autocomplete="off">
    <button class="p" onclick="entrar()">Entrar</button>
    <button onclick="carregar()">Atualizar</button>
  </div>
  <div id="msg"></div>
  <div class="cards" id="cards"></div>
  <h2>Contas cadastradas</h2>
  <div id="accs"></div>
  <h2>Ativos por dia (14 dias)</h2>
  <div id="dau"></div>
  <h2>Versoes em uso (30 dias)</h2>
  <div id="vers"></div>
</div>
<script>
  var BASE = location.origin;
  function tk(){ return localStorage.getItem('tk') || ''; }
  function entrar(){ localStorage.setItem('tk', document.getElementById('tk').value.trim()); carregar(); }
  function msg(m){ document.getElementById('msg').textContent = m || ''; }
  function esc(s){ return (s==null?'':String(s)).replace(/[&<>]/g, function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;'}[c];}); }
  async function api(path, opts){
    opts = opts || {}; opts.headers = Object.assign({'authorization':'Bearer '+tk()}, opts.headers||{});
    var r = await fetch(BASE+path, opts);
    if(r.status===401){ msg('Token invalido ou nao informado.'); throw new Error('401'); }
    return r.json();
  }
  async function carregar(){
    if(!tk()){ msg('Cole o ADMIN_TOKEN e clique Entrar.'); return; }
    msg('Carregando...');
    try{
      var s = await api('/admin/stats');
      var t = s.totais||{};
      document.getElementById('cards').innerHTML =
        card(t.ativos_hoje,'Ativos hoje')+card(t.instalacoes,'Instalacoes')+
        card(t.contas_ativas,'Contas ativas')+card(t.contas_revogadas,'Revogadas');
      document.getElementById('dau').innerHTML = tabela(['Dia','Ativos'], (s.dau||[]).map(function(d){return [d.day, d.ativos];}));
      document.getElementById('vers').innerHTML = tabela(['Versao','Canal','Aparelhos'], (s.versoes||[]).map(function(v){return [v.version||'-', v.channel||'-', v.n];}));
      var a = await api('/admin/accounts');
      renderAccs(a.accounts||[]);
      msg('');
    }catch(e){ if(e.message!=='401') msg('Erro: '+e.message); }
  }
  function card(n,l){ return '<div class="c"><div class="n">'+(n==null?0:n)+'</div><div class="l">'+l+'</div></div>'; }
  function tabela(cols, rows){
    if(!rows.length) return '<div class="mut" style="padding:10px">Sem dados.</div>';
    var h='<table><tr>'+cols.map(function(c){return '<th>'+c+'</th>';}).join('')+'</tr>';
    h+=rows.map(function(r){return '<tr>'+r.map(function(c){return '<td>'+esc(c)+'</td>';}).join('')+'</tr>';}).join('');
    return h+'</table>';
  }
  function renderAccs(list){
    if(!list.length){ document.getElementById('accs').innerHTML='<div class="mut" style="padding:10px">Ninguem cadastrado ainda.</div>'; return; }
    var h='<table><tr><th>Telefone</th><th>Status</th><th>Nome</th><th>Maquina</th><th>Criado</th><th>Ultimo login</th><th>Acoes</th></tr>';
    h+=list.map(function(a){
      var st = a.status==='revoked' ? '<span class="tag r">revogado</span>' : '<span class="tag a">ativo</span>';
      var mac = a.machine ? '<span class="mut">'+esc(String(a.machine).slice(0,10))+'...</span>' : '<span class="mut">-</span>';
      var acts = '<div class="act">'+
        (a.status==='revoked'
          ? '<button onclick="setStatus(\\''+a.phone+'\\',\\'active\\')">Reativar</button>'
          : '<button onclick="setStatus(\\''+a.phone+'\\',\\'revoked\\')">Revogar</button>')+
        '<button onclick="resetMaquina(\\''+a.phone+'\\')">Resetar PC</button></div>';
      return '<tr><td>'+esc(a.phone)+'</td><td>'+st+'</td><td>'+esc(a.nome)+'</td><td>'+mac+'</td><td class="mut">'+esc((a.created_at||'').slice(0,10))+'</td><td class="mut">'+esc((a.last_login||'').slice(0,16).replace('T',' '))+'</td><td>'+acts+'</td></tr>';
    }).join('');
    document.getElementById('accs').innerHTML = h+'</table>';
  }
  async function setStatus(phone, status){
    await api('/admin/revoke',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({phone:phone,status:status})});
    carregar();
  }
  async function resetMaquina(phone){
    if(!confirm('Desamarrar a maquina de '+phone+'? Ele podera logar em outro PC.')) return;
    await api('/admin/reset-machine',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({phone:phone})});
    carregar();
  }
  document.getElementById('tk').value = tk();
  if(tk()) carregar();
</script>
</body></html>`;
