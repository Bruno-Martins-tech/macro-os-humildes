# Macro Os Humildes - Documentacao Completa

## Visao Geral

App de desktop Windows que grava e repete sequencias de mouse e teclado para automatizar tarefas repetitivas no WYD Global (Server 3). Distribuido como um unico `.exe` self-contained — o usuario nao instala nada.

**Autor do projeto:** Bruno
**Creditos no app:** MartinS-
**Guilda:** Os Humildes
**Plataforma:** Windows 10/11

---

## Stack Tecnica

- **Linguagem:** C# / .NET 8 / WinForms
- **Distribuicao:** Single-file self-contained (`PublishSingleFile=true`, `SelfContained=true`, `win-x64`)
- **Permissao:** Manifesto `requireAdministrator` (necessario pra enviar cliques a jogos que rodam como admin)
- **Dependencias externas:** Nenhuma (Win32 via P/Invoke)
- **Musica:** MCI Player (winmm.dll) tocando `login.mp3` direto da pasta do WYD
- **Auto-update:** GitHub Releases API (checagem automatica ao abrir + botao manual)
- **Persistencia:** JSON em `%APPDATA%\MacroOsHumildes\macros.json`

---

## Estrutura do Projeto

```
MacroOsHumildes/
  MacroOsHumildes.csproj   # Config do projeto .NET 8
  app.manifest              # Manifesto UAC (requireAdministrator)
  app.ico                   # Icone bandeira do Brasil (16/32/48/256px)
  Program.cs                # Entry point
  MainForm.cs               # Toda a logica (UI, hooks, gravacao, reproducao, hotkeys, updater)
  README.md                 # Instrucoes de build e uso
  docs/DOCS.md              # Esta documentacao
  dist/                     # Pasta de distribuicao
    MacroOsHumildes.exe
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

Persistido em `%APPDATA%\MacroOsHumildes\macros.json` (System.Text.Json, indentado).

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
- Compara `tag_name` (ex: "v1.6.0") com `CURRENT_VERSION` no codigo
- Se versao nova: popup → baixa `.exe` → renomeia atual pra `.bak` → substitui → reinicia com `Verb = "runas"`
- Timeout de 8s na checagem (nao trava o app se nao tiver internet)
- Botao "Atualizar" na barra de abas pra forcar checagem manual

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
- **Header:** Gradiente + bandeira do Brasil (GDI+) + runas nordicas decorativas + titulo dourado "With Your Destiny"
- **Layout:** Cards com cantos arredondados (CardPanel custom)
- **Botoes:** ModernButton com hover effect e borda de acento
- **Abas:** MACROS / COMO USAR (tutorial integrado com 5 passos)
- **Status bar:** Bolinha colorida (verde=pronto, amarelo=aguardando, vermelho=rodando)
- **Rodape:** Botoes Discord, Droplist, Updates WYD + creditos
- **Musica:** login.mp3 do WYD em loop (volume baixo, botao mute)
- **Icone:** Bandeira do Brasil multi-resolucao (16/32/48/256px)

---

## Como Compilar

### Pre-requisitos

.NET 8 SDK instalado em `C:\Users\bnobm\.dotnet\` (instalado via script oficial Microsoft).

### Build

```bash
C:\Users\bnobm\.dotnet\dotnet.exe publish MacroOsHumildes.csproj -c Release
```

Saida: `bin\Release\net8.0-windows\win-x64\publish\MacroOsHumildes.exe` (~69MB, self-contained)

Para publicar em pasta especifica:
```bash
C:\Users\bnobm\.dotnet\dotnet.exe publish MacroOsHumildes.csproj -c Release -o publish_vXYZ
```

---

## Como Publicar uma Atualizacao

1. Alterar `CURRENT_VERSION` em `MainForm.cs` (ex: `"1.7.0"`)
2. Compilar: `dotnet publish -c Release -o publish_v170`
3. Commit + push:
   ```
   git add MainForm.cs
   git commit -m "feat: v1.7.0 - descricao"
   git push origin master
   ```
4. Criar release no GitHub:
   ```
   gh release create v1.7.0 publish_v170/MacroOsHumildes.exe --title "v1.7.0 - Titulo" --notes "Descricao"
   ```
5. Todos os usuarios recebem aviso de atualizacao ao abrir o app

**Repo:** https://github.com/Bruno-Martins-tech/macro-os-humildes

---

## Distribuicao

### Para novos usuarios

1. Gerar o zip: copiar `.exe` + `LEIA-ME.txt` para pasta `dist/`, zipar
2. O zip fica em `C:\Users\bnobm\OneDrive\Desktop\Macro-Os-Humildes.zip`
3. Mandar no Discord/WhatsApp da guilda

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

Criado por **MartinS-** para a guilda **Os Humildes** — WYD Global Server 3.
