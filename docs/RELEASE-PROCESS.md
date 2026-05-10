# Overtake Telemetry — Processo de Release

> **A partir da v1.1.31** o processo é orquestrado pelo GitHub Actions e funciona inteiramente do macOS / qualquer máquina com `gh` CLI. O fluxo Windows local com `.\scripts\Release.ps1` continua disponível como alternativa, mas não é mais necessário.

## TL;DR — Release a partir do Mac

```bash
# 1. Você editou o código numa branch (ex.: fix/xyz). Bumpou:
#    - src/Overtake.SimHub.Plugin/Properties/AssemblyInfo.cs
#    - CHANGELOG.md (nova seção `## [X.Y.Z] - YYYY-MM-DD`)
#    - version.json (campo "version" e "released")

# 2. Push da branch + abre PR
git push -u origin fix/xyz
gh pr create --base main --title "vX.Y.Z: …" --body-file <(cat <<'EOF'
## Summary
- ...
EOF
)

# 3. CI roda automaticamente (build + 17 testes). Espera ficar verde.
gh pr checks --watch

# 4. Merge no main
gh pr merge --merge --delete-branch

# 5. Atualiza local + cria tag
git checkout main && git pull
git tag -a vX.Y.Z -m "vX.Y.Z: short summary"
git push origin vX.Y.Z

# 6. release.yml dispara: build + ConfuserEx + Inno Setup + GitHub Release com instalador anexado.
#    Acompanhe:
gh run watch
```

A partir desse ponto, o GitHub Release `vX.Y.Z` aparece em
[`/releases`](https://github.com/drakokot-oss/overtake-simhub-plugin/releases)
com o `Overtake.SimHub.Plugin-vX.Y.Z-Setup.exe` anexado, pronto para usuários
baixarem e para o `version.json` apontar.

---

## Pipeline GitHub Actions

### `.github/workflows/ci.yml` — em todo PR e push para `main`

1. Restaura cache do SimHub portable (download direto do GitHub Releases oficial — `SHWotever/SimHub@9.11.12`, ~227 MB) e ConfuserEx
2. Resolve `SIMHUB_INSTALL_PATH` apontando para a pasta extraída
3. Compila `src/Overtake.SimHub.Plugin/Overtake.SimHub.Plugin.csproj` em Release com MSBuild
4. Roda `scripts/Test-SessionStore.ps1` e `scripts/Test-Finalizer.ps1` (Test 17 é a regressão crítica do v1.1.31 byte-boxing cast bug)
5. Faz upload da DLL como artifact (retenção de 14 dias)

Falhou? PR fica vermelho — não merge.

### `.github/workflows/release.yml` — em push de tag `v*.*.*`

1. **Verificação de consistência:** `AssemblyInfo.cs`, `version.json` e a tag têm que coincidir. Se discordarem, falha imediatamente (proteção contra "esqueci de bumpar X").
2. Mesmo build + testes do `ci.yml` (defesa em profundidade — não confia que o CI já passou no PR).
3. **ConfuserEx** (cached): aplica `confuser.crproj` (constantes + control flow + rename + anti-tamper).
4. **Inno Setup** instalado via `choco install innosetup -y`. Compila `dist/v{VERSION}/installer.iss` (gera o arquivo do template mais recente se não existir).
5. **CHANGELOG extraction:** lê a seção `## [VERSION]` do `CHANGELOG.md` e usa como corpo do GitHub Release.
6. **Cria GitHub Release** via `softprops/action-gh-release@v2`, anexando `Overtake.SimHub.Plugin-vX.Y.Z-Setup.exe`.
7. Sobe o instalador também como artifact (retenção 90 dias) — útil para download direto sem passar pela Releases page.

---

## Pré-requisitos no Mac (uma vez)

```bash
# 1. Instalar gh CLI (sem Homebrew, sem sudo)
mkdir -p ~/.local/bin
curl -fsSL "https://github.com/cli/cli/releases/latest/download/gh_$(curl -fsSL https://api.github.com/repos/cli/cli/releases/latest | grep tag_name | head -1 | sed -E 's/.*"v([^"]+)".*/\1/')_macOS_$(uname -m | sed s/x86_64/amd64/).zip" -o /tmp/gh.zip
unzip -d /tmp/gh-extract /tmp/gh.zip
cp $(find /tmp/gh-extract -type f -name gh -perm +111 | head -1) ~/.local/bin/gh
chmod +x ~/.local/bin/gh

# 2. Garantir ~/.local/bin no PATH (zsh)
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc
source ~/.zshrc

# 3. Autenticar (uma vez)
gh auth login   # escolha GitHub.com → HTTPS → login via browser
```

Pronto. Daqui em diante o release é totalmente do Mac.

---

## Versionamento

Semantic Versioning (X.Y.Z):

| Parte | Quando incrementar | Exemplo |
|-------|-------------------|---------|
| **X** (Major) | Mudança incompatível no JSON schema do `.otk` | 1.0.0 → 2.0.0 |
| **Y** (Minor) | Novo recurso (campo novo, funcionalidade) | 1.1.0 → 1.2.0 |
| **Z** (Patch) | Correção de bug, hotfix de UI | 1.1.0 → 1.1.1 |

Antes de criar a tag você **precisa** bumpar manualmente:

| Arquivo | O que muda |
|---|---|
| `src/Overtake.SimHub.Plugin/Properties/AssemblyInfo.cs` | `AssemblyVersion` e `AssemblyFileVersion` para `X.Y.Z.0` |
| `CHANGELOG.md` | Nova seção `## [X.Y.Z] - YYYY-MM-DD` no topo |
| `version.json` | Campos `version`, `released`, `installerUrl` (apontando para a tag), `releaseNotes` (resumo curto, escapado) |

Por que manual? Para evitar bumps acidentais e manter as release notes humanas — não geradas por máquina. O `release.yml` valida que tudo bate com a tag antes de prosseguir.

---

## Onde cada arquivo é usado

| Arquivo | Localização | Função |
|---------|-------------|--------|
| `version.json` | Raiz do repo, servido por `raw.githubusercontent.com/.../main/version.json` | Plugin instalado no SimHub do usuário verifica no startup para notificar updates |
| `Overtake.SimHub.Plugin-vX.Y.Z-Setup.exe` | Anexado ao GitHub Release `vX.Y.Z` | Instalador final para usuários (link gerado automaticamente bate com `installerUrl` do version.json) |
| `CHANGELOG.md` | Raiz do repo | Fonte de verdade das release notes; o `release.yml` extrai a seção da versão e usa como corpo do GitHub Release |
| `dist/v{X.Y.Z}/installer.iss` | Pasta `dist/` no repo | Template Inno Setup. Se não existir, o `release.yml` gera a partir do template da versão anterior. Pode ser commitado quando há mudança na lógica do instalador |
| `confuser.crproj` | Raiz do repo | Configuração de obfuscação do ConfuserEx; raramente muda |

---

## Verificação pós-release (3 cliques no GitHub)

1. **GitHub Release page:** abra `https://github.com/drakokot-oss/overtake-simhub-plugin/releases` — confirme que `vX.Y.Z` apareceu com o instalador `.exe` anexado e o tamanho razoável (~5-10 MB)
2. **Workflow run:** `gh run list --workflow release.yml --limit 1` deve mostrar status SUCCESS. Se houve warnings (ex.: nome do instalador diferente do esperado), revise antes de divulgar.
3. **Update notification:** abra o SimHub local com a versão anterior; o plugin deve detectar a v nova em < 1 min (cache do GitHub raw é ~30 s).

---

## Plano B — Build local no Windows (legacy)

Se o GitHub Actions estiver fora do ar ou se você quiser depurar o build localmente, o fluxo antigo continua funcionando. Pré-requisitos no Windows:

- SimHub instalado em `C:\Program Files (x86)\SimHub\` (provê as DLLs de referência)
- Visual Studio Build Tools (MSBuild com workload .NET Framework 4.8) — ou rode dentro de "Developer PowerShell for VS"
- Inno Setup 6 em `C:\Program Files (x86)\Inno Setup 6\`
- ConfuserEx CLI em `tools/ConfuserEx/Confuser.CLI.exe` (opcional — sem ele o build sai sem obfuscação)

```powershell
.\scripts\Build-Package.ps1
```

O `Build-Package.ps1` agora resolve MSBuild via `Get-Command msbuild.exe` → `vswhere` → fallback `.NET Framework 4.0` (em ordem) e respeita `$env:SIMHUB_INSTALL_PATH`. Funciona tanto em dev box quanto em runner GitHub Actions, mas o pipeline do `release.yml` não chama esse script — ele orquestra os passos diretamente para evitar acoplamento com a lógica de auto-bump de versão.

---

## Troubleshooting

| Problema | Causa & solução |
|---|---|
| `release.yml` falhou em "Verify version consistency" | Bumpe `AssemblyInfo.cs`, `version.json` e/ou adicione a seção no `CHANGELOG.md`, depois recrie a tag (`git tag -d vX.Y.Z && git push origin :vX.Y.Z` e refaça) |
| `release.yml` falhou em "ConfuserEx failed" | A versão pinada (`CONFUSEREX_VERSION` em `release.yml`) saiu do ar ou mudou estrutura. Confira https://github.com/mkaring/ConfuserEx/releases e atualize |
| `release.yml` falhou em "ISCC failed" | A `installer.iss` tem syntax error ou referência a arquivo inexistente. Rode local com `ISCC.exe dist\v{VERSION}\installer.iss` |
| GitHub Release criado mas sem asset anexado | `INSTALLER_PATH` ficou vazio — provavelmente ISCC não encontrou nenhum `*Setup*.exe`. Cheque o log da etapa "Compile Inno Setup installer" |
| Update notification não chega aos usuários | Cache do GitHub raw (~30 s); a `installerUrl` do `version.json` precisa bater EXATAMENTE com o nome do asset no Release |
| CI demora muito (>10 min) | Primeira execução baixa SimHub portable (227 MB). Próximas usam cache (~10 s). Se cache invalida muito, o cache key inclui `SIMHUB_VERSION` — só muda quando você bumpa `SIMHUB_VERSION` em `ci.yml` / `release.yml` |
