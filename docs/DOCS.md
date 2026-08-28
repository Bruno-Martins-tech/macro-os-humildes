# Macro Supremes - Documentacao Completa

## Visao Geral

App de desktop Windows que grava e repete sequencias de mouse e teclado para automatizar tarefas repetitivas no WYD Global (Server 3). Distribuido como um unico `.exe` self-contained — o usuario nao instala nada.

**Autor do projeto:** Bruno
**Creditos no app:** MartinS-
**Guilda:** Supremes
**Plataforma:** Windows 10/11

---

## Stack Tecnica

- **Linguagem:** C# / .NET 8 / WinForms
- **Distribuicao:** Single-file self-contained (`PublishSingleFile=true`, `SelfContained=true`, `win-x64`)
- **Permissao:** Manifesto `requireAdministrator` (necessario pra enviar cliques a jogos que rodam como admin)
- **Dependencias externas:** Nenhuma (Win32 via P/Invoke)
- **Musica:** MCI Player (winmm.dll) tocando `login.mp3` direto da pasta do WYD
- **Auto-update:** GitHub Releases API (checagem automatica ao abrir + botao manual)
- **Persistencia:** JSON em `%APPDATA%\MacroSupremes\macros.json`

---

## Estrutura do Projeto

```
MacroSupremes/
  MacroOsHumildes.csproj   # Config do projeto .NET 8
  app.manifest              # Manifesto UAC (requireAdministrator)
  app.ico                   # Icone brasao da guild (16/32/48/64/128/256px)
  brasao.jpg                # Brasao da guild Supremes (header do app)
  Program.cs                # Entry point
  MainForm.cs               # Toda a logica (UI, hooks, gravacao, reproducao, hotkeys, updater)
  README.md                 # Instrucoes de build e uso
  docs/DOCS.md              # Esta documentacao
  dist/                     # Pasta de distribuicao
    MacroSupremes.exe
    LEIA-ME.txt
```

---

## Arquitetura

### Modelo de Dados

```
MacroEvent { T, Type, X, Y, Button, Key, Down, Wheel }
Macro { Name, Hotkey, Repeticoes, IntervaloMs, AtrasoInicialMs, Eventos[] }
Biblioteca { Macros[] }
```

Persistido em `%APPDATA%\MacroSupremes\macros.json` (System.Text.Json, indentado).

### Gravacao (Win32 low-level hooks)

- Mouse: `SetWindowsHookEx(WH_MOUSE_LL)` — captura move, cliques L/R/M, scroll
- Teclado: `SetWindowsHookEx(WH_KEYBOARD_LL)` — captura todas as teclas
- Delegates dos hooks mantidos em campos da classe (evita GC coletar)
- Tempo medido com `Stopwatch`
- Movimentos amostrados a cada ~20ms
- Contagem regressiva de 3s antes de iniciar
- ESC para gravacao e NAO e gravado

### Reproducao (SendInput)

- Movimento: `SetCursorPos(x, y)`
- Cliques: `SendInput` com `INPUT_MOUSE` + flags MOUSEEVENTF_*
- Scroll: `SendInput` com `MOUSEEVENTF_WHEEL`
- Teclas: `SendInput` com `INPUT_KEYBOARD`
- Roda em `Task.Run` (thread separada) com `volatile bool reproduzindo`
- Sleep cancelavel (pedacos de 50ms checando a flag)

### Hotkeys Globais

- `RegisterHotKey` para cada macro com atalho (F5-F8, F10-F12)
- F9 = toggle gravacao do macro selecionado
- Ctrl+F12 = botao de panico (para tudo)
- Tratados via `WM_HOTKEY` no `WndProc`
- Re-registrados quando atalhos mudam; desregistrados ao fechar

### Auto-Updater

- Checa `https://api.github.com/repos/Bruno-Martins-tech/macro-os-humildes/releases/latest`
- Compara `tag_name` (ex: "v1.8.0") com `CURRENT_VERSION` no codigo
- Se versao nova: popup → baixa `.exe` → renomeia atual pra `.bak` → substitui → reinicia
- Timeout de 8s na checagem (nao trava o app se nao tiver internet)
- Botao "Atualizar" na barra de abas pra forcar checagem manual

### Hack Login Server Full

- Botao toggle na aba Config
- Ativa proxy `0.0.0.4:80` via Registry (`HKCU\Internet Settings`)
- Notifica o sistema via `InternetSetOption` (wininet.dll)
- Permite logar quando o servidor esta lotado
- Deve ser desativado apos logar

---

## Macros Padrao (seed na 1a execucao)

| Nome | Hotkey | IntervaloMs | Eventos |
|------|--------|-------------|---------|
| Auto Pergaminho da Agua | F5 | 3000 | vazio (usuario grava) |
| Auto Up de Montaria | F6 | 3000 | vazio (usuario grava) |
| Auto Chat (divulgacao) | F7 | 12000 | Enter + Seta cima + Enter (pre-preenchido) |
| Auto-uso de item | F8 | 30000 | vazio (usuario grava) |

---

## Atalhos

| Tecla | Funcao |
|-------|--------|
| F5-F8 | Iniciar/parar macro correspondente (toggle) |
| F9 | Iniciar/parar gravacao do macro selecionado |
| Ctrl+F12 | Botao de panico: para tudo imediatamente |
| ESC | Para a gravacao (durante gravacao) |

---

## UI / Design

- **Tema:** Dark mode com tons azul-roxo escuro (atmosfera WYD)
- **Header:** Gradiente + brasao da guild Supremes + runas nordicas decorativas + titulo dourado "With Your Destiny"
- **Layout:** Cards com cantos arredondados (CardPanel custom)
- **Botoes:** ModernButton com hover effect e borda de acento
- **Abas:** MACROS / COMO USAR (tutorial integrado com 5 passos) / CONFIG
- **Status bar:** Bolinha colorida (verde=pronto, amarelo=aguardando, vermelho=rodando)
- **Rodape:** Botoes Discord, Droplist, Updates WYD + creditos
- **Musica:** login.mp3 do WYD em loop (volume baixo, botao mute)
- **Icone:** Brasao da guild multi-resolucao (16/32/48/64/128/256px)

---

## Como Compilar

### Pre-requisitos

.NET 8 SDK instalado em `C:\Users\bnobm\.dotnet\` (instalado via script oficial Microsoft).

### Build

```bash
C:\Users\bnobm\.dotnet\dotnet.exe publish MacroOsHumildes.csproj -c Release
```

Saida: `bin\Release\net8.0-windows\win-x64\publish\MacroSupremes.exe` (~69MB, self-contained)

Para publicar em pasta especifica:
```bash
C:\Users\bnobm\.dotnet\dotnet.exe publish MacroOsHumildes.csproj -c Release -o publish_vXYZ
```

---

## Como Publicar uma Atualizacao

1. Alterar `CURRENT_VERSION` em `MainForm.cs` (ex: `"1.8.0"`)
2. Compilar: `dotnet publish -c Release -o publish_v180`
3. Commit + push:
   ```
   git add MainForm.cs
   git commit -m "feat: v1.8.0 - descricao"
   git push origin master
   ```
4. Criar release no GitHub:
   ```
   gh release create v1.8.0 publish_v180/MacroSupremes.exe --title "v1.8.0 - Titulo" --notes "Descricao"
   ```
5. Todos os usuarios recebem aviso de atualizacao ao abrir o app

**Repo:** https://github.com/Bruno-Martins-tech/macro-os-humildes

---

## Canal de Staging (testes sem afetar a guild)

Existe um canal separado pra testar mudancas antes de mandar pra guild. E o MESMO codigo,
ligado por um simbolo de build. Nada da staging chega em quem usa a stable.

**Como buildar staging:**
```
dotnet publish -c Release -p:Staging=true -o publish_staging
```
Gera `MacroSupremes-Staging.exe`.

**O que muda no build staging (isolamento total):**
- **Nome do exe:** `MacroSupremes-Staging.exe` (roda lado a lado com a stable)
- **Pasta de dados:** `%APPDATA%\MacroSupremes-Staging\` (macros/config/logs proprios; nao mistura com a stable)
- **Canal de update:** puxa **pre-releases** do GitHub (a stable so pega o `latest`, que exclui pre-release)
- **Titulo da janela:** ganha o selo `[STAGING]`

**Fluxo recomendado:**
1. Testar na staging: `gh release create v1.12.0-beta1 publish_staging/MacroSupremes-Staging.exe --prerelease --title "..." --notes "..."`
2. So o app staging (que o dono roda) recebe esse pre-release. A guild na stable nao ve.
3. Validado, promover pra stable: build normal (`dotnet publish -c Release`) + `gh release create v1.12.0 ... ` **sem** `--prerelease`.

Ponto unico de controle no codigo: classe `Canal` em `MainForm.cs` (`Canal.PastaApp`, `Canal.EhStaging`, `Canal.SufixoTitulo`).

---

## Distribuicao

### Para novos usuarios

1. Gerar o zip: copiar `.exe` + `LEIA-ME.txt` para pasta `dist/`, zipar
2. Mandar no Discord/WhatsApp da guilda

### Para usuarios existentes

O app atualiza sozinho via GitHub Releases. Nao precisa mandar zip de novo.

---

## Historico de Versoes

| Versao | Mudancas |
|--------|----------|
| 1.1.0 | Versao inicial com auto-updater, musica WYD, tema nordico |
| 1.2.0 | Server 3 no header |
| 1.3.0 | Botoes Droplist e Updates WYD no rodape |
| 1.4.0 | Icone bandeira BR, botao forcar update |
| 1.5.0 | Fix restart apos update (UAC), icone multi-resolucao correto |
| 1.6.0 | Feedback de gravacao, botao salvar macro, botao limpar gravacao |
| 1.7.0 | Aba Config com atalhos personalizaveis e velocidade |
| 1.8.0 | Rebrand Supremes, brasao da guild, hack login server full, fix restart |
| 1.9.0-1.11.0 | Anti-DC (rede/CPU + monitor de ping), fix "fecha e nao reabre", release seguro |
| 1.12.0 | Backend Cloudflare (telemetria + licenca telefone/senha + painel admin com contas e DCs + alerta de patch no Discord); canal de staging; brasao embutido; nome corrigido p/ SUPREMUS; redesenho de UX (cards com profundidade, dourado, passos numerados, overlay 3-2-1, faixa REC, lista com status/atalho, tela de boas-vindas, tutorial reescrito); volume da musica; Wi-Fi Maximo corrigido (powercfg) |

---

## Links Rapidos

- **Repo GitHub:** https://github.com/Bruno-Martins-tech/macro-os-humildes
- **Releases:** https://github.com/Bruno-Martins-tech/macro-os-humildes/releases/latest
- **Droplist WYD:** https://droplist.raidhut.com/
- **Updates WYD:** https://wydglobal.raidhut.com/pt-br/3578

---

## Riscos / Uso Responsavel

- O app so simula mouse/teclado — nao modifica o jogo, nao le memoria, nao injeta pacotes
- Cada servidor de WYD tem suas regras — spam de chat e autoclick AFK podem gerar mute/ban
- Nao acessa a internet (exceto checar updates no GitHub e abrir links quando o usuario clica)

---

Criado por **MartinS-** para a guilda **Supremes** — WYD Global Server 3.
