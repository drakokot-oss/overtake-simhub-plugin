# Release process

A v1.1.30 introduziu **release automatizado via GitHub Actions**. Você não precisa mais estar no Windows para gerar e publicar uma nova versão do plugin.

---

## TL;DR — como criar um release

A partir de qualquer máquina (Mac, Linux ou Windows):

```bash
# 1) Bump dos 4 arquivos de versão (única coisa manual):
#
#    src/Overtake.SimHub.Plugin/Properties/AssemblyInfo.cs
#       → AssemblyVersion("X.Y.Z.0") + AssemblyFileVersion("X.Y.Z.0")
#
#    CHANGELOG.md
#       → adicionar `## [X.Y.Z] - YYYY-MM-DD` no topo com descrição do release
#
#    version.json
#       → "version": "X.Y.Z"
#       → "released": "YYYY-MM-DD"
#       → "installerUrl": "https://github.com/drakokot-oss/overtake-simhub-plugin/releases/download/vX.Y.Z/Overtake.SimHub.Plugin-vX.Y.Z-Setup.exe"
#       → "releaseNotes": "<resumo curto pra notificação dentro do plugin>"
#
#    dist/v1.1.19/installer.iss
#       → #define MyAppVersion "X.Y.Z"

# 2) commit + push (via PR ou direto na main, como preferir)
git add -A
git commit -m "release: vX.Y.Z"
git push origin main

# 3) criar e fazer push da tag — isso dispara o workflow Release
git tag vX.Y.Z
git push origin vX.Y.Z
```

Em ~5 minutos o GitHub Actions:

1. **Valida** que os 4 arquivos de versão batem com a tag (falha rápido se você esqueceu de bumpar algum)
2. Baixa o SimHub (cacheado entre runs)
3. Compila o `.dll` em Release com MSBuild
4. Roda os 3 test suites (`Test-Parsers.ps1`, `Test-SessionStore.ps1`, `Test-Finalizer.ps1`)
5. Empacota `.simhubplugin`, gera o instalador Inno Setup (`*-Setup.exe`) e o ZIP do site
6. Cria a GitHub Release com os 3 artefatos anexados

A partir desse momento:
- **Plugin instalado:** mostra "Update available" automaticamente (consome `version.json` na `main`)
- **Site `racehub.overtakef1.com/downloads`:** atualiza automaticamente apontando para o novo `*-Setup.exe`

---

## Workflows

### `.github/workflows/release.yml`

| Trigger | Ação |
|---|---|
| `git push origin vX.Y.Z` (tag) | Build, test, package, GitHub Release |
| `workflow_dispatch` (botão na UI do GitHub) | Mesmo, com input opcional de versão |

Etapas internas:

1. **Resolve target version** — extrai `X.Y.Z` da tag ou do input manual
2. **Verify version files match tag** — falha cedo se algum dos 4 arquivos não bater com a tag: `AssemblyInfo.cs`, `version.json` (version + installerUrl + releaseNotes), `CHANGELOG.md`, `installer.iss`
3. **Cache SimHub binaries** — `actions/cache@v4` salva 216MB de SimHub (chave `simhub-{version}-v3`); subsequentes runs pulam download
4. **Setup MSBuild** + **Install Inno Setup 6.2.2** (via Chocolatey)
5. **Build (Release)** — `msbuild` direto, `PostBuildEvent=` vazio
6. **Run tests** — todos os 3 scripts; falha se qualquer um quebrar
7. **Build installer + package** — chama `scripts/Build-Package.ps1 -SkipTests`
8. **Extract release notes from CHANGELOG.md** — pega a seção `## [X.Y.Z]`
9. **Upload build artifacts (debug)** — guarda artifacts por 30 dias (acessíveis na UI)
10. **Create GitHub Release** — `softprops/action-gh-release@v2`; sobe instalador + `.simhubplugin` + `OvertakeTelemetry-vX.Y.Z.zip`

### `.github/workflows/ci.yml`

Roda em todo push de `main` e em PRs. Faz build + tests, sem publicar nada. Usa o mesmo cache do SimHub.

---

## Pré-requisitos no GitHub

Nenhum. O `GITHUB_TOKEN` que o Actions injeta automaticamente já tem permissão para criar Releases (declarado em `permissions: contents: write` no workflow).

---

## Como o SimHub é resolvido no CI

O `.csproj` referencia DLLs do SimHub via `$(SimHubInstallPath)`. No CI:

1. O workflow baixa `SimHub.9.11.11.zip` de `https://www.simhubdash.com/official/`
2. Extrai em `<workspace>/simhub/`
3. Define `SIMHUB_INSTALL_PATH=<workspace>\simhub\` antes de `msbuild`
4. As DLLs ficam em cache do Actions; runs futuros levam ~10 segundos só pra restaurar

Se a EA/SimHub Wotever um dia remover essa versão do mirror, basta atualizar `SIMHUB_VERSION` e `SIMHUB_URL` nos dois workflows. Versões antigas costumam ficar para sempre no `/official/`.

---

## Trabalho local (Windows) ainda funciona

Os scripts `scripts/Build-Package.ps1` e `scripts/Release.ps1` continuam funcionando normalmente no Windows. Os workflows do GitHub Actions e o processo local são caminhos paralelos e independentes — você pode usar um, o outro, ou ambos.

---

## Erros comuns

| Sintoma | Causa | Correção |
|---|---|---|
| `::error::AssemblyInfo.cs: AssemblyVersion does not match X.Y.Z.0` | Tag criada antes do bump de versão | Apague a tag (`git tag -d vX.Y.Z; git push origin :vX.Y.Z`), bumpe os arquivos, recommit, retag |
| `::error::version.json: installerUrl is '...'` | URL do `version.json` não bate com `https://github.com/<repo>/releases/download/v<X.Y.Z>/Overtake.SimHub.Plugin-v<X.Y.Z>-Setup.exe` | Ajuste manualmente — o nome do instalador é determinístico (vem do `installer.iss` `OutputBaseFilename`) |
| `::error::CHANGELOG.md: no '## [X.Y.Z]' entry` | Esqueceu de adicionar a entrada no changelog | Adicione `## [X.Y.Z] - YYYY-MM-DD` no topo |
| `Required SimHub DLL not found in extracted archive` | Estrutura interna do zip mudou (raríssimo) | Atualize `SIMHUB_VERSION`/`SIMHUB_URL` nos dois workflows para uma versão compatível |
| Tests falham na CI mas passam local | Diferença de runner Windows vs sua máquina | Olhe o artifact `build-artifacts-vX.Y.Z` na run que falhou para baixar a DLL e reproduzir local |
| `softprops/action-gh-release` falha com 403 | Permission `contents: write` ausente | Já está declarada no workflow; checar settings do repo (Settings → Actions → Workflow permissions: Read and write) |

---

## Quem está fora desse pipeline

- **ConfuserEx (obfuscation):** o build local opcionalmente obfuscava o DLL via `tools\ConfuserEx\Confuser.CLI.exe`. O workflow do CI não roda obfuscation por padrão. Se quiser adicionar, baixe o ConfuserEx no step de install e copie para `tools/ConfuserEx/`. O `Build-Package.ps1` já procura nesse path.
- **Install local em SimHub (`Release.ps1` step 8):** óbvio, não roda no CI. Só faz sentido na sua máquina.
