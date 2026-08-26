# MACRO SUPREMES

App de automacao de mouse e teclado para WYD. Grava e repete sequencias de cliques e teclas.
Com musica nostalgica do WYD e auto-update via GitHub.

## Como gerar o .exe

O .NET 8 SDK ja esta instalado em `C:\Users\bnobm\.dotnet\`.

```
C:\Users\bnobm\.dotnet\dotnet.exe publish -c Release
```

O executavel fica em: `bin\Release\net8.0-windows\win-x64\publish\MacroSupremes.exe`

## Para quem recebe o .exe

- Baixe e de 2 cliques. Como o app pede admin, vai aparecer o **UAC** — clique "Sim".
- Pode aparecer aviso do **SmartScreen** — clique em "Mais informacoes" e depois "Executar assim mesmo".
- Os macros de **pergaminho**, **montaria** e **item** precisam ser **gravados uma vez** (as posicoes da tela variam por PC). O **Auto Chat** ja vem pronto.
- O app checa atualizacoes automaticamente ao abrir.

## Macros que ja vem prontos

| Macro | Atalho | O que faz |
|-------|--------|-----------|
| Auto Pergaminho da Agua | F5 | Vazio — grave seus cliques no inventario |
| Auto Up de Montaria | F6 | Vazio — grave seus cliques |
| Auto Chat (divulgacao) | F7 | Enter + Seta cima + Enter (reenvia ultima msg a cada 12s) |
| Auto-uso de item | F8 | Vazio — grave o clique no slot do item |

## Atalhos

- **F5 a F8** — Inicia/para o macro correspondente (funciona com o jogo em foco)
- **F9** — Inicia/para gravacao do macro selecionado
- **Ctrl + F12** — Botao de panico: para tudo imediatamente
- **ESC** — Para a gravacao (alternativa ao F9)

## Como publicar uma atualizacao

1. Altere a versao nos **tres** lugares (mantenha iguais): `CURRENT_VERSION` em `MainForm.cs`,
   `<Version>`/`<FileVersion>` em `MacroOsHumildes.csproj` e `assemblyIdentity version` em `app.manifest`.
2. Compile: `dotnet publish -c Release`
3. Gere o **SHA-256** do exe: `certutil -hashfile "bin\Release\net8.0-windows\win-x64\publish\MacroSupremes.exe" SHA256`
4. Va no GitHub → Releases → "Create a new release"
5. Tag: `v1.11.0` (mesmo numero das constantes, com "v" na frente)
6. **Anexe SOMENTE o `MacroSupremes.exe` standalone** da pasta `publish` — **NUNCA** o instalador
   (`MacroSupremes-Setup.exe`). O auto-update baixa o primeiro `.exe` que **nao** seja setup/install/instalador;
   se voce anexar o Setup, os usuarios trocam o app pelo instalador.
7. Cole o **SHA-256** no corpo/nota do release (rastreabilidade; o app valida a integridade do proprio download).
8. Publique.

Todos os usuarios que abrirem o app vao receber o aviso de atualizacao automaticamente.

### Como o update se aplica (a prova de "fecha e nao reabre")
- Ao clicar em atualizar, o app baixa a nova versao pra `MacroSupremes.exe.update` (valida tamanho + cabecalho MZ + grava `.sha256`).
- Um script robusto espera o exe destravar, faz o swap com **rollback** e reabre o app; se algo falhar, o app **continua aberto**.
- Como rede de seguranca, ao abrir o app ele aplica qualquer `.update` pendente **antes** da tela (se falhar, abre normal mesmo assim).
- Trilha de diagnostico em `%AppData%\MacroSupremes\logs\update-log.txt`.

**Repo:** `github.com/Bruno-Martins-tech/macro-os-humildes`

## Aviso

Use conforme as regras do seu servidor. O app apenas simula mouse e teclado — nao modifica o jogo, nao le memoria e nao injeta pacotes.

---

Criado por **MartinS-** para a guilda **Supremes**.
