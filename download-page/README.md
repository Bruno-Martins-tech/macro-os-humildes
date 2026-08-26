# Página de download — Macro Supremes

Landing page pública com o passo a passo de instalação (pensada para leigos), na identidade visual do app (brasão + paleta escura/verde da guilda Supremes).

- **No ar:** https://macro-supremes-download.pages.dev
- **Hospedagem:** Cloudflare Pages (projeto `macro-supremes-download`)
- **Arquivos:** `index.html` (página, CSS embutido) + `brasao.jpg` (logo)

## Como o download se mantém atualizado
O botão aponta para `releases/latest/download/MacroSupremes-Setup.exe`. Ou seja, **quando uma release nova é publicada, a página já baixa a versão nova** — não precisa editar nada aqui.

## Como editar/republicar
1. Edite o `index.html` (e/ou troque o `brasao.jpg`).
2. Publique de novo:

```powershell
npx wrangler pages deploy download-page --project-name macro-supremes-download --branch main --commit-dirty=true
```

Requer o Cloudflare autenticado no `wrangler` (`npx wrangler whoami` para conferir).

## Gerar o instalador anexado à release
O `MacroSupremes-Setup.exe` é gerado pelo Inno Setup a partir de `../installer.iss` e anexado ao Release (o auto-updater ignora assets com "setup" no nome):

```powershell
# 1) publicar o app (gera bin\Release\...\publish\MacroSupremes.exe)
dotnet publish -c Release
# 2) compilar o instalador -> dist\MacroSupremes-Setup.exe
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
# 3) anexar na release
gh release upload vX.Y.Z dist\MacroSupremes-Setup.exe --repo Bruno-Martins-tech/macro-os-humildes --clobber
```
