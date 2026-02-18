# Overtake Telemetry — Processo de Release

## TL;DR — Um comando faz tudo

Após fazer alterações no plugin com o Cursor, basta rodar:

```powershell
.\scripts\Release.ps1
```

Isso automaticamente:
1. Incrementa a versão (1.1.1 → 1.1.2)
2. Compila o plugin
3. Roda todos os testes (105 testes)
4. Gera os pacotes de distribuição
5. Atualiza o `version.json` (para notificação de update)
6. Faz commit + tag no Git
7. Push para o GitHub
8. Instala a DLL no SimHub local

**Nenhuma ação manual necessária.**

---

## Fluxo Completo (o que acontece internamente)

```
┌─────────────────────────────────────────────────────────┐
│  Você faz alterações no Cursor com o agente IA          │
│  (código, UI, lógica, correções)                        │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  .\scripts\Release.ps1                                  │
│                                                         │
│  [1] Bump versão       AssemblyInfo.cs atualizado       │
│  [2] Build + Test      MSBuild Release + 105 testes     │
│  [3] Package           .simhubplugin + ZIP + version    │
│  [4] Git commit + tag  "release: vX.Y.Z"                │
│  [5] Git push          GitHub origin/main + tag         │
│  [6] Install local     DLL copiada para SimHub          │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  GitHub (automático)                                    │
│                                                         │
│  version.json acessível em:                             │
│  raw.githubusercontent.com/.../main/version.json        │
│                                                         │
│  Conteúdo: { "version": "1.2.0", "download": "..." }   │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  Usuários existentes (automático)                       │
│                                                         │
│  Ao abrir SimHub → plugin verifica version.json         │
│  Se versão nova > versão instalada:                     │
│    → Mostra "Update available! Download vX.Y.Z"         │
│    → Link para racehub.overtakef1.com/downloads         │
└───────────────────────┬─────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  Upload manual (única etapa não automatizada)           │
│                                                         │
│  Arquivo: dist/OvertakeTelemetry-vX.Y.Z.zip            │
│  Destino: racehub.overtakef1.com/downloads              │
│                                                         │
│  O ZIP contém:                                          │
│    - Install-OvertakeTelemetry.bat (instalador GUI)     │
│    - Overtake.SimHub.Plugin.dll                         │
│    - README.txt (inglês)                                │
│    - LEIAME.txt (português)                             │
└─────────────────────────────────────────────────────────┘
```

---

## Opções do Release.ps1

| Comando | O que faz |
|---------|-----------|
| `.\scripts\Release.ps1` | Release completo com auto-bump de patch |
| `.\scripts\Release.ps1 -Version "2.0.0"` | Versão explícita |
| `.\scripts\Release.ps1 -SkipTests` | Pula testes (hotfix rápido) |
| `.\scripts\Release.ps1 -NoPush` | Não faz push para GitHub |
| `.\scripts\Release.ps1 -NoInstall` | Não instala no SimHub local |
| `.\scripts\Release.ps1 -NoPush -NoInstall` | Só build + package local |

---

## Versionamento

O plugin usa **Semantic Versioning** (X.Y.Z):

| Parte | Quando incrementar | Exemplo |
|-------|-------------------|---------|
| **X** (Major) | Mudança incompatível no JSON schema | 1.0.0 → 2.0.0 |
| **Y** (Minor) | Novo recurso (campo novo, funcionalidade) | 1.1.0 → 1.2.0 |
| **Z** (Patch) | Correção de bug, ajuste de UI | 1.1.0 → 1.1.1 |

O script **auto-incrementa o patch** por padrão. Para minor/major, use `-Version`.

---

## Onde cada arquivo é usado

| Arquivo | Localização | Função |
|---------|-------------|--------|
| `version.json` | Raiz do repo (GitHub) | Plugin verifica no startup para notificar updates |
| `dist/OvertakeTelemetry-vX.Y.Z.zip` | Gerado pelo build | ZIP para upload no site |
| `dist/Overtake.SimHub.Plugin.dll` | Gerado pelo build | DLL avulsa para install manual |
| `dist/Install-OvertakeTelemetry.bat` | Fonte no repo | Instalador GUI para usuários |
| `dist/README.txt` / `LEIAME.txt` | Fonte no repo | Instruções no ZIP |
| `AssemblyInfo.cs` | `src/.../Properties/` | Versão do assembly (.NET) |

---

## Verificação pós-release

Após rodar o Release.ps1, verifique:

1. **Terminal** — Script terminou com "RELEASE vX.Y.Z COMPLETE"
2. **SimHub** — Abra e veja a versão no plugin (canto superior direito)
3. **GitHub** — `version.json` atualizado: `https://raw.githubusercontent.com/drakokot-oss/overtake-simhub-plugin/main/version.json`
4. **Website** — Faça upload do ZIP em `racehub.overtakef1.com/downloads`

---

## Fluxo típico de trabalho

```
1. Abrir Cursor
2. Pedir alterações ao agente IA (ex: "adicione campo X ao JSON")
3. Agente faz as alterações no código
4. Rodar: .\scripts\Release.ps1
5. Upload do ZIP no site (se necessário)
6. Pronto — usuários recebem notificação de update
```

---

## Troubleshooting

| Problema | Solução |
|----------|---------|
| "BUILD FAILED" | Verifique erros no output. Corrija e rode novamente. |
| "Tests FAILED" | Alguma lógica quebrou. O agente IA pode corrigir. |
| Push falhou | `git push origin main --tags` manualmente. |
| SimHub não encontrado | Instale manualmente: copie a DLL para a pasta do SimHub. |
| Versão no plugin não mudou | Feche e reabra o SimHub após instalar. |
| Update não aparece para usuários | Aguarde ~1 min (cache do GitHub). Verifique a URL do version.json. |
