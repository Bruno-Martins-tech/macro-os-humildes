-- ============================================================================
-- Macro Supremes - backend Cloudflare (D1 / SQLite)
-- Telemetria de uso + licenciamento (telefone+senha) + estado do vigia de patch
-- Aplicar:  wrangler d1 execute macro-supremes --file=./schema.sql
-- ============================================================================

-- Contas de licenca. Login = telefone (numero do WhatsApp) + senha da pessoa.
-- Cadastro aberto: qualquer um cria; o dono modera depois (status revoked).
CREATE TABLE IF NOT EXISTS accounts (
  phone       TEXT PRIMARY KEY,          -- so digitos, normalizado (ex: 5519999998888)
  pass_hash   TEXT NOT NULL,             -- PBKDF2(senha, salt)
  pass_salt   TEXT NOT NULL,             -- salt aleatorio por conta (hex)
  machine     TEXT,                      -- hash do MachineGuid do PC amarrado (null ate 1o login)
  status      TEXT NOT NULL DEFAULT 'active',  -- active | revoked
  nome        TEXT,                      -- rotulo opcional que o dono coloca
  created_at  TEXT NOT NULL,
  last_login  TEXT
);

-- Base instalada: 1 linha por maquina. Da distribuicao de versoes e total de instalacoes.
CREATE TABLE IF NOT EXISTS devices (
  machine     TEXT PRIMARY KEY,          -- hash do MachineGuid
  phone       TEXT,                      -- conta associada (se logada), senao null
  version     TEXT,
  channel     TEXT,                      -- stable | staging
  first_seen  TEXT NOT NULL,
  last_seen   TEXT NOT NULL,
  seen_count  INTEGER NOT NULL DEFAULT 1
);

-- Usuarios ativos por dia (DAU). PK (dia, maquina) = conta unica por dia automatica.
CREATE TABLE IF NOT EXISTS heartbeats (
  day         TEXT NOT NULL,             -- YYYY-MM-DD (UTC)
  machine     TEXT NOT NULL,
  version     TEXT,
  channel     TEXT,
  PRIMARY KEY (day, machine)
);

-- Relatorios de desconexao (Anti-DC) por maquina/dia. Guarda o MAX do dia
-- (resiliente a restart do app, que zera os contadores da sessao).
CREATE TABLE IF NOT EXISTS dc_reports (
  day         TEXT NOT NULL,
  machine     TEXT NOT NULL,
  dc_count    INTEGER NOT NULL DEFAULT 0,
  spike_count INTEGER NOT NULL DEFAULT 0,
  ping_medio  INTEGER,
  wyd_abertos INTEGER,
  updated_at  TEXT NOT NULL,
  PRIMARY KEY (day, machine)
);

-- Estado do vigia de patch do WYD (o cron guarda a ultima assinatura vista).
CREATE TABLE IF NOT EXISTS wyd_patch_state (
  id             INTEGER PRIMARY KEY CHECK (id = 1),
  last_signature TEXT,
  last_checked   TEXT
);

CREATE INDEX IF NOT EXISTS idx_devices_lastseen ON devices(last_seen);
CREATE INDEX IF NOT EXISTS idx_heartbeats_day   ON heartbeats(day);
CREATE INDEX IF NOT EXISTS idx_accounts_machine ON accounts(machine);
