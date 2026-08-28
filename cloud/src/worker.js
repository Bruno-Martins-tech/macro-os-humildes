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
