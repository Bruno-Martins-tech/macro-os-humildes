# Backend Cloudflare - Macro Supremes

Um Worker (free tier) que cobre **telemetria de uso**, **licenciamento** (telefone+senha) e o
**vigia de patch do WYD** (cron que avisa no Discord). Banco em **D1** (SQLite).

## Rotas

| Rota | Metodo | Para que |
|------|--------|----------|
| `/heartbeat` | POST | App manda sinal anonimo ao abrir (maquina hasheada + versao + canal). Alimenta DAU e base instalada. |
| `/license/register` | POST | Cria conta: `{phone, password, machine}`. Cadastro aberto. |
| `/license/validate` | POST | Valida `{phone, password, machine}` a cada abertura. Amarra 1 conta = 1 PC. |
| `/admin/stats` | GET | Ativos por dia, versoes, totais. **Bearer ADMIN_TOKEN**. |
| `/admin/accounts` | GET | Lista contas. **Admin**. |
| `/admin/revoke` | POST | `{phone, status?}` revoga (ou reativa) uma conta. **Admin**. |
| `/admin/reset-machine` | POST | `{phone}` desamarra a maquina (pessoa trocou de PC). **Admin**. |

Cron `0 12 * * 4` (quinta ~09h BRT): compara a pagina de updates do WYD; se mudou, posta no Discord.

## Painel de Admin (visao do dono)

Abra no navegador: **https://macro-supremes.bno-bmartins.workers.dev/admin**
Cole o `ADMIN_TOKEN` e clique Entrar. Mostra quem cadastrou, ativos por dia, versoes em uso,
e deixa **revogar/reativar** conta e **resetar a maquina** (quando a pessoa troca de PC) com um clique.
O token fica salvo no navegador (localStorage) so nesse aparelho.

## Deploy (primeira vez)

```bash
cd cloud
npx wrangler d1 create macro-supremes          # copie o database_id pro wrangler.toml
npx wrangler d1 execute macro-supremes --remote --file=./schema.sql
npx wrangler secret put ADMIN_TOKEN            # um token forte que so voce sabe
npx wrangler secret put DISCORD_WEBHOOK_URL    # webhook do canal do Discord da guild
npx wrangler deploy
```

A URL publica sai no fim do `deploy` (ex: `https://macro-supremes.<subdominio>.workers.dev`).
Essa URL vai no app (classe `Backend` em `MainForm.cs`).

## Redeploy

```bash
cd cloud && npx wrangler deploy
```

## Consultas rapidas (sem admin, direto no D1)

```bash
npx wrangler d1 execute macro-supremes --remote --command "SELECT day, COUNT(*) FROM heartbeats GROUP BY day ORDER BY day DESC LIMIT 14"
npx wrangler d1 execute macro-supremes --remote --command "SELECT version, COUNT(*) FROM devices GROUP BY version"
```

## Notas

- **Senha**: guardada como PBKDF2-SHA256 (100k iteracoes) + salt por conta. Nunca em texto puro.
- **Privacidade**: `machine` e um hash do MachineGuid, nao da pra reverter pro PC. Telemetria nao guarda nome/telefone (isso so fica em `accounts`, pro login).
- **Free tier**: Workers 100k req/dia, D1 5M leituras/dia. Folga enorme pra uma guild.
- **Vigia de patch**: usa hash do texto da pagina. Se der alarme falso (a pagina mexe em algo dinamico), estreitar o trecho hasheado em `checarPatchWyd`.
