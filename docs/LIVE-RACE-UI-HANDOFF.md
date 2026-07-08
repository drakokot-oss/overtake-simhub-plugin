# Live Race UI — Documentacao completa de handoff

> **Onde esta este arquivo:** `docs/LIVE-RACE-UI-HANDOFF.md`  
> **Caminho absoluto (Mac):** `/Users/wagner.pereira/Documents/Plugin2.0/overtake-simhub-plugin/docs/LIVE-RACE-UI-HANDOFF.md`  
> **Status git (Jun/2026):** arquivo **local, ainda nao commitado** — por isso nao aparece no GitHub/remote ate ser adicionado ao repo.

Documento de continuidade para retomar o trabalho numa nova conversa ou por outro dev.
Cobre contexto, decisoes, o que foi construido, estado do codigo, backlog e proximos passos.

**Branch de trabalho:** `feat/race-ui-test-build`  
**Versao base do plugin:** `1.1.47.0` (build de teste exclusivo — **NAO produtizado**)  
**Documento relacionado (cloud/futuro):** `docs/LIVE-CLOUD-BROADCAST-DESIGN.md`  
**Repo:** `drakokot-oss/overtake-simhub-plugin`

---

## Indice

1. [Contexto e origem do projeto](#1-contexto-e-origem-do-projeto)
2. [Decisoes de arquitetura](#2-decisoes-de-arquitetura)
3. [Resumo do que foi entregue](#3-resumo-do-que-foi-entregue)
4. [Arquitetura tecnica](#4-arquitetura-tecnica)
5. [Como usar (usuario final / narrador)](#5-como-usar-usuario-final--narrador)
6. [Schema WebSocket (snapshot JSON)](#6-schema-websocket-snapshot-json)
7. [Dashboard — abas e funcionalidades](#7-dashboard--abas-e-funcionalidades)
8. [Overlays OBS](#8-overlays-obs)
9. [Backend — detalhes de implementacao](#9-backend--detalhes-de-implementacao)
10. [Historico de features por fase](#10-historico-de-features-por-fase)
11. [Estado do codigo e commits](#11-estado-do-codigo-e-commits)
12. [Build de teste (NAO e release)](#12-build-de-teste-nao-e-release)
13. [Backlog e proximos passos](#13-backlog-e-proximos-passos)
14. [Problemas conhecidos](#14-problemas-conhecidos)
15. [Investigacao Austria .otk (penalidades duplicadas)](#15-investigacao-austria-otk-penalidades-duplicadas)
16. [Preview local sem SimHub](#16-preview-local-sem-simhub)
17. [Como retomar numa nova conversa](#17-como-retomar-numa-nova-conversa)

---

## 1. Contexto e origem do projeto

### Linha do tempo

| Data / fase | O que aconteceu |
|-------------|-----------------|
| Pre-Race UI | Plugin v1.1.47 focado em captura `.otk`, F1 26 readiness, Sprint, My Team |
| Analise Austria | Importacao do `.otk` mostrava penalidades dobradas no site — confirmado origem no arquivo, nao no front |
| Decisao v1.1.48 | Fix de dedup de penalidades no export **adiado** — coletar mais casos (My Team, Sprint, 100%) antes de release grande |
| Planejamento UI | Pedido de UI grande no SimHub, benchmark Track Titan, foco inicial **engenharia/espectador** (nao coaching piloto ainda) |
| Validacao design | Mock HTML iterativo antes de codigo C# — aprovado "Rota B" |
| v1 test build | Build exclusivo Wagner via `test-build.yml`, sem atualizar site/downloads |
| Testes reais v1 | Feedback: logo, persistencia pos-sessao, voltas no drawer, destaques, ERS, avisos vs pen |
| v2 test build | 13 melhorias (setores live, track map, forecast, pit prediction, etc.) |
| v2b | Stints, modo ERS, Principais Brigas, F1_26 label, 6 overlays OBS |
| v2c (local) | Phantom fix, drawer avisos/punicoes detalhados, track map click fix, aba Overlay com iframe |

### Objetivo do produto (fase atual)

Interface **broadcast / engenharia** em tempo real para quem roda o plugin:
- Narradores e comentaristas
- Engenheiros de equipe acompanhando corrida
- Uso local (browser) ou captura OBS

**Fora de escopo nesta fase:** coaching piloto-a-piloto (Track Titan-like), cloud publico (planejado separadamente).

### Garantia importante

A Race UI e **read-only** em relacao ao pipeline de export:
- Continua gerando `.otk` identico ao de hoje
- `RaceWebServer` + `LiveSnapshotBuilder` nao alteram `SessionStore` nem `LeagueFinalizer` no caminho de export

---

## 2. Decisoes de arquitetura

### Rota escolhida: **Rota B — Web UI servida pelo plugin**

| Opcao | Descricao | Decisao |
|-------|-----------|---------|
| A | Widgets nativos SimHub (WPF) | Rejeitada — limitada para layout broadcast |
| B | Plugin hospeda HTTP + WebSocket + HTML/JS | **Escolhida** — controle total, OBS-friendly |
| C | Site Overtake consome UDP direto | Rejeitada nesta fase — requer infra cloud |

### Por que TcpListener manual (nao HttpListener)?

- Evita exigencia de URL ACL / admin no Windows
- Zero dependencias extras no host .NET Framework 4.8 do SimHub
- WebSocket hand-rolled com framing texto JSON

### Superficie de renderizacao

- **Dashboard:** `http://localhost:8088/` → `race-ui.html`
- **Overlays OBS:** `http://localhost:8088/overlays?view=<nome>`
- **Assets:** `/logo.png`, `/snapshot` (JSON ultimo estado, debug)

### Auto-switch Qualy / Corrida

Checkbox "Auto Quali/Corrida" (default ON): quando `session.mode` muda entre `qualy` e `race`, a aba ativa troca automaticamente entre Classificacao e Transmissao.

---

## 3. Resumo do que foi entregue

| Entregavel | Status |
|------------|--------|
| Servidor web embarcado (`RaceWebServer`) | Feito |
| Builder snapshot live (`LiveSnapshotBuilder`) | Feito |
| Dashboard 4 abas (`race-ui.html`) | Feito |
| 6 overlays OBS (`overlays.html`) | Feito |
| Track Map (Motion packet 0) | Feito |
| Settings SimHub (enable/port/LAN) | Feito |
| CI build de teste (`test-build.yml`) | Feito |
| Doc cloud broadcast | Feito (`LIVE-CLOUD-BROADCAST-DESIGN.md`) |
| Doc handoff (este arquivo) | Feito (local) |
| Release publica v1.1.48+ | **Pendente** validacao Wagner |

---

## 4. Arquitetura tecnica

```
F1 Game (UDP porta configurada, tipicamente 20778)
    |
    v
UdpReceiver -> PacketParser -> SessionStore.Ingest()
    |                              |
    | Pacotes live UI:             | Pipeline export (inalterado)
    |  0 Motion (track map)        v
    |  1 Session                   LeagueFinalizer -> OtkWriter -> .otk
    |  2 LapData
    |  3 Events (PENA, etc.)
    |  4 Participants
    |  7 CarStatus
    |  8 FinalClassification
    | 10 CarDamage
    | 11 SessionHistory
    v
LiveSnapshotBuilder.Build(store, sessionId)
    |
    v
RaceWebServer.Publish(json)  ----WebSocket---->  race-ui.html
    |                                            overlays.html
    v
HTTP GET /, /overlays, /logo.png, /snapshot
```

### Componentes novos (referencia de arquivos)

| Arquivo | Papel |
|---------|-------|
| `Live/LiveSnapshotBuilder.cs` | Monta snapshot JSON: session, grid[], events[] |
| `Live/RaceWebServer.cs` | TcpListener, handshake WS, push broadcast, static files |
| `Assets/race-ui.html` | Dashboard principal (embedded resource) |
| `Assets/overlays.html` | 6 overlays OBS (embedded resource) |
| `Assets/overtake-icon.png` | Logo `/logo.png` |
| `Packets/MotionData.cs` | Parse packet ID 0 — WorldX, WorldZ, Yaw |
| `Packets/LapDataEntry.cs` | + LapDistance para track map |
| `Store/DriverRun.cs` | Campos live: LiveS1/S2, LiveWorldX/Z, LiveYaw, etc. |
| `Store/SessionRun.cs` | TotalLaps, TrackLength, WeatherForecast, etc. |
| `Store/SessionStore.cs` | IngestMotion, ingest live fields |
| `Finalizer/LeagueFinalizer.cs` | `IsPhantomForLive()` wrapper publico |
| `OvertakeSettings.cs` | RaceUiEnabled, RaceUiPort, RaceUiAllowLan |
| `OvertakePlugin.cs` | Lifecycle: start/stop server, publish loop |
| `UI/SettingsControl.xaml(.cs)` | Secao "Race UI (web) - BUILD DE TESTE" |
| `.github/workflows/test-build.yml` | Build manual -> artefato installer teste |

---

## 5. Como usar (usuario final / narrador)

### Setup no SimHub

1. Instalar build de teste (artefato CI) ou build local
2. Abrir **SimHub → Overtake Telemetry → Settings**
3. Secao **"Race UI (web) - BUILD DE TESTE"**:
   - Marcar **Race UI Enabled**
   - Porta: default `8088`
   - **Allow LAN**: marcar se outros PCs na rede precisam acessar
4. URL exibida nas settings: ex. `http://localhost:8088/`
5. Abrir no Chrome/Edge ou adicionar como Browser Source no OBS

### Fluxo tipico de transmissao

1. Entrar no lobby / sessao no F1
2. Abrir dashboard no browser secundario ou OBS
3. Aba **Transmissao** durante corrida; **Classificacao** no qualy (auto se checkbox ON)
4. Clicar piloto na tabela → drawer com telemetria detalhada
5. Aba **Track Map** → clicar carro no mapa ou dropdown → painel lateral
6. Aba **Overlay** → preview dos overlays + copiar URLs para OBS
7. Ao fim da corrida: dados ficam 10 min na tela; qualy fica indefinidamente

### OBS — overlays

Cada overlay e URL separada com **fundo transparente**:

```
http://localhost:8088/overlays?view=tower
http://localhost:8088/overlays?view=lower
http://localhost:8088/overlays?view=battle
http://localhost:8088/overlays?view=driver&car=0
http://localhost:8088/overlays?view=weather
http://localhost:8088/overlays?view=gaps
```

Configurar Browser Source: largura conforme tabela secao 8, altura proporcional, **sem** fundo/opacidade no OBS (HTML ja e transparente).

---

## 6. Schema WebSocket (snapshot JSON)

Publicado a ~2 Hz (frequencia do loop do plugin). Clientes recebem texto JSON.

### Envelope

```json
{
  "ok": true,
  "tsMs": 1719750000000,
  "session": { ... },
  "grid": [ ... ],
  "events": [ ... ]
}
```

Quando `ok: false` (sem sessao ativa): UI **nao apaga** ultimo snapshot valido — mostra "Sessao encerrada - aguardando proxima".

### session

| Campo | Tipo | Descricao |
|-------|------|-----------|
| `game` | string | `F1_25`, `F1_26`, etc. (content-aware) |
| `trackName` | string | Nome pista |
| `sessionTypeName` | string | Ex. "Corrida", "Q1" |
| `mode` | string | `race` ou `qualy` |
| `currentLap` | int | Volta atual (corrida) |
| `totalLaps` | int | Total voltas |
| `trackLength` | int | Metros |
| `timeLeftSec` | int? | Tempo restante (qualy/practice) |
| `weather` | int | 0-5 condicao atual |
| `trackTempC`, `airTempC` | int | Temperaturas |
| `safetyCar` | int | Status SC |
| `rainNextPct` | int? | Proxima chuva % |
| `forecast` | array | `[{ offsetMin, weather, rainPct, trackTempC, airTempC }]` |

### grid[] (por piloto)

| Campo | Descricao |
|-------|-----------|
| `carIdx` | Indice carro UDP (0-21) |
| `pos`, `grid` | Posicao corrida / largada |
| `tag`, `team` | Nome piloto / equipe |
| `lastLapMs`, `bestLapMs`, `curLapMs` | Tempos |
| `liveS1`, `liveS2` | Setores ao vivo nesta volta |
| `lastS1/S2/S3`, `pbS1/S2/S3` | Setores ultima volta / PB |
| `intervalMs`, `gapMs` | Intervalo / gap lider |
| `compound` | Pneu atual (S/M/H/I/W) |
| `stints` | Array compostos usados ex. `["M","H"]` |
| `tyreAge` | Voltas no pneu |
| `tyreWear` | `{ avg, max, fl, fr, rl, rr }` — UI usa `max` |
| `damage` | `{ wingFL, wingFR, wingRear, worst }` |
| `stops` | Paradas box |
| `ersPct`, `ersMode` | Bateria % e modo (Nenhum/Medio/Volta Rapida/Boost) |
| `fuelKg`, `fuelLaps` | Combustivel |
| `penaltiesSec` | Total segundos penalidade |
| `penalties` | `[{ type, timeSec, lap, desc }]` |
| `warningsDetail` | `[{ lap, desc }]` — avisos type 5 |
| `warnings`, `cornerCutWarnings` | Contagens agregadas |
| `pitRejoinPos`, `pitLossSec` | Estimativa rejoin se parar agora |
| `x`, `z`, `yaw`, `lapDist` | Track map (Motion) |
| `laps` | Ultimas 5 voltas `[{ n, ms, s1, s2, s3, deltaMs }]` |
| `status` | Em pista / Pit / Finalizado / etc. |

### events[]

Feed lateral: `PENA`, `OVTK`, `FTLP`, `SCAR`, `COLL`, `RTMT`, `SSTA`, `SEND`, etc. Eventos `PENA` incluem `data.desc` em PT.

---

## 7. Dashboard — abas e funcionalidades

### Aba Transmissao (`broadcast`)

- Tabela corrida: pos, piloto (+/- posicoes), pneu (stints), desgaste max, intervalo, gap, tempos, ERS+modo, combustivel (voltas destaque), danos, pits, avisos, pen(s), status
- Popovers em avisos (corte vs outros) e punicoes (descricao PT)
- Painel **Principais Brigas**: pares com intervalo < 1.0s
- Feed de eventos da corrida
- Strip **Previsao** clima (agora, +5, +10, +15, +30 min)

### Aba Classificacao (`qualy`)

- Tabela setores com cores F1: roxo (session best), verde (PB pessoal), amarelo (mais lento)
- Setores atualizam **ao vivo** conforme piloto completa S1/S2 (nao so ao fim da volta)
- Melhores setores sessao + volta ideal

### Aba Track Map (`trackmap`)

- Canvas: outline da pista acumulado de posicoes Motion (~1 volta para forma real)
- Carros como dots coloridos por equipe
- **Selecionar piloto:** dropdown OU clique no carro (mousedown)
- Painel lateral: mesmo detalhe do drawer

**Nota preview mock:** elipse fake; em corrida real o tracado vem das coordenadas UDP.

**Fix v2c (local):** throttle redraw 250ms, canvas nao reseta dimensao a cada frame, select so rebuilda se lista pilotos mudar.

### Aba Overlay (`overlay`)

- Sub-abas: Tower, Lower, Battle, Driver, Weather, Gaps
- **Preview iframe** ao vivo (`/overlays?view=...`)
- Grid com URLs para copiar no OBS

*(Antes v2c: aba mostrava mini-tower legado; links OBS ficavam no rodape — corrigido.)*

### Drawer piloto (clique na tabela)

- Header: nome, equipe, posicao
- Melhor / ultima / volta atual
- Posicao, grid, pneu (stints)
- Estimativa pit rejoin (corrida)
- ERS, combustivel, desgaste 4 pneus, danos
- **Resumo** avisos/punicoes
- **Tabela avisos detalhados** (`warningsDetail`)
- **Tabela punicoes aplicadas** (DT, SG, tempo, cumpridos, etc.)
- Ultimas 5 voltas com delta (sem scroll)

### Persistencia pos-sessao

| Tipo sessao | Comportamento |
|-------------|---------------|
| Qualy | Dados permanecem ate nova sessao |
| Corrida | Grace **10 min** apos fim, depois tela "Sessao finalizada" |
| ok:false | Mantem ultimo snapshot na tela |

---

## 8. Overlays OBS

Servidos em `/overlays?view=<nome>` (hash `#nome` funciona em preview file://).

| view | Descricao | Tamanho OBS sugerido |
|------|-----------|---------------------|
| `tower` | Timing tower vertical estilo F1 TV | 330 x 600 |
| `lower` | Faixa inferior: lider, volta, FL, bandeira | 1280 x 80 |
| `battle` | Head-to-head + delta (auto: par mais proximo) | 560 x 120 |
| `driver` | Card piloto foco | 380 x 400 |
| `weather` | Clima + forecast + alerta chuva | 430 x 200 |
| `gaps` | Grafico gaps ao lider top 6 | 760 x 350 |

**Parametros URL:**

| Param | Overlay | Exemplo |
|-------|---------|---------|
| `?car=N` | driver | Piloto carIdx N |
| `?a=N&b=M` | battle | Par fixo |

**Limitacao preview local:** iframe na aba Overlay so funciona via HTTP do plugin (`localhost:8088`), nao via `file://`.

---

## 9. Backend — detalhes de implementacao

### Motion (packet 0)

- `MotionEntry.Parse()` — WorldX @0, WorldZ @8, Yaw @48
- `IngestMotion`: so drivers ja mapeados (evita phantoms)

### Stints de pneu

- Fonte: `DriverRun.TyreStints` (`List<Dictionary>`) de SessionHistory (11) e FinalClassification (8)
- Snapshot expoe `stints: ["M","H"]` via `StintCodes()`
- Fallback: compound atual se history ainda nao chegou
- **Cuidado:** nao criar segundo campo `TyreStints` byte — causou erro CS0102

### Phantom cars

- `LeagueFinalizer.IsPhantomForLive()` — wrapper de `IsPhantomEntry`
- Live grid usa mesma heuristica do export `.otk`
- Fix commit `69a86fd`: eliminou 4 carros fantasmas em teste Wagner

### Label F1_26

`GameLabel()` prioriza **conteudo** sobre wire format:
- Teams ID 220-230 (`GameInfo.IsF1_26TeamId`)
- Track Madring ID 42
- Fallback: `GameNameFromPacketFormat`

Motivo: "2026 Season Pack" roda dentro F1 25 com UDP format 2025.

### Penalidades

| Camada | Comportamento |
|--------|---------------|
| Live UI contagem | `DedupPenaltyCount` — dedup por type+infringement+lap+sec |
| Live UI listas | `PenaltyList()` + `WarningList()` com desc PT |
| Export `.otk` | **Sem dedup completo** — backlog v1.1.48 |

`PenaltySnapshots` populados em eventos `PENA`, `DTSV`, `SGSV`, `COLL`.

Modos ERS: `ErsModeName()` → Nenhum / Medio / Volta Rapida / Boost.

### Pit rejoin prediction

Heuristica: perda fixa ~22s (`PitLossSecForTrack`), compara gap projetado com grid — estimativa espectador, nao simulacao F1.

---

## 10. Historico de features por fase

### Fase v1
- [x] Logo Overtake
- [x] Persistencia pos-sessao
- [x] Drawer com historico voltas
- [x] Destaques
- [x] ERS bar cores
- [x] Avisos vs Pen(s) separados

### Fase v2 (13 itens)
- [x] Setores qualy ao vivo + cores F1
- [x] Track Map (substitui Estrategia)
- [x] Forecast clima
- [x] Combustivel: voltas destaque
- [x] Delta posicao +/-
- [x] Coluna Pits
- [x] Popovers punicoes/avisos descritivos
- [x] Pit rejoin prediction
- [x] "Lider" no GAP
- [x] ERS ranges 0-15-25-60
- [x] Desgaste = pneu mais gasto
- [x] Grace 10min pos-corrida
- [x] Cap 5 voltas drawer

### Fase v2b
- [x] Stints pneu coluna
- [x] Modo ERS abaixo barra
- [x] Principais Brigas
- [x] Track map clicavel (v1)
- [x] F1_26 content label
- [x] 6 overlays OBS

### Fase v2c (**alteracoes locais — ver secao 11**)
- [x] Phantom filter live grid
- [x] Drawer: tabelas avisos + punicoes detalhadas
- [x] Track map: throttle, mousedown, select signature
- [x] Aba Overlay: iframe preview + URLs (nao rodape)
- [x] PenaltyList expandido + WarningList backend
- [x] overlays.html: hash routing para preview file://

---

## 11. Estado do codigo e commits

### Commits na branch `feat/race-ui-test-build` (remoto)

```
4363f05 Live UI: add 6 broadcast overlays (OBS-ready)
5dc47dc fix(build): reuse existing DriverRun.TyreStints for live stint chips
4d1baa6 Live UI: tyre stints, ERS mode, battles, clickable map, F1 26 label
69a86fd fix(live): filter phantom/AI-filler cars from broadcast grid
e9c1d4c test: de-flake Test 48 Sprint-Format terminal ordering
1803160 Race UI v2: live sectors, Track Map, forecast, pit prediction + more
f4a4962 feat(race-ui): logo, persist, lap history, highlights, ERS, warnings/penalties
09b5e40 feat(race-ui): live broadcast web UI (Rota B) - test build only
```

### Alteracoes locais NAO commitadas (Jun/2026)

| Arquivo | Conteudo |
|---------|----------|
| `docs/LIVE-RACE-UI-HANDOFF.md` | **Este documento** (untracked) |
| `Assets/race-ui.html` | v2c: overlay tab iframe, track map fix, drawer avisos/punicoes |
| `Assets/overlays.html` | hash routing |
| `Live/LiveSnapshotBuilder.cs` | WarningList, PenaltyList expandido |

**Ultimo CI verde remoto:** run `28420032704` (overlays + stints; **nao inclui v2c local**).

---

## 12. Build de teste (NAO e release)

```bash
cd /Users/wagner.pereira/Documents/Plugin2.0/overtake-simhub-plugin
gh workflow run test-build.yml --repo drakokot-oss/overtake-simhub-plugin --ref feat/race-ui-test-build
gh run list --repo drakokot-oss/overtake-simhub-plugin --workflow test-build.yml --limit 1
gh run watch <RUN-ID> --repo drakokot-oss/overtake-simhub-plugin --exit-status
```

Artefato: `Overtake-TEST-Setup-v1.1.47-test` (download na pagina Actions).

**NAO tocar:** `version.json`, `CHANGELOG.md`, tag release, site downloads.

Produtizar quando Wagner aprovar: PR → bump versao → release pipeline normal (ver `.cursor/rules/release-workflow.mdc`).

---

## 13. Backlog e proximos passos

### Alta prioridade

| # | Item | Notas |
|---|------|-------|
| 1 | Commitar v2c + este doc | Para nao perder handoff |
| 2 | Validar stints pos-pit corrida real | SessionHistory pode atrasar online |
| 3 | Track map circuito real | Precisa ~1 volta Motion |
| 4 | Testar overlays OBS | Browser source transparente |
| 5 | Seletor piloto/par nos overlays | Hoje so URL `?car=` / `?a=&b=` |
| 6 | Penalty dedup `.otk` | **v1.1.48** — mais casos teste |

### Media prioridade

| # | Item |
|---|------|
| 7 | Merge branch → PR → release pos-validacao |
| 8 | Atualizar `SPECTATOR-GUIDE.md` com Race UI |
| 9 | Testes unitarios `LiveSnapshotBuilder` |
| 10 | Reduzir payload WS antes cloud broadcast |

### Baixa prioridade (fase 2+)

| # | Item |
|---|------|
| 11 | Cloud broadcast publico — `LIVE-CLOUD-BROADCAST-DESIGN.md` |
| 12 | UI coaching piloto (Track Titan-like) |
| 13 | Overlay pit/strategy dedicado |
| 14 | Telemetria raw SimHub |

---

## 14. Problemas conhecidos

| Problema | Status | Mitigacao |
|----------|--------|-----------|
| Doc handoff nao no GitHub | Local untracked | Commitar `docs/LIVE-RACE-UI-HANDOFF.md` |
| Penalidades duplicadas `.otk` | Backlog v1.1.48 | Live deduplica contagem |
| Preview overlays iguais (file://) | Corrigido v2c | Usar `#battle` nao `?view=` |
| Track map click impossivel | Corrigido v2c local | Throttle + mousedown |
| SessionHistory lento online | Conhecido | Stints fallback compound atual |
| Motion UDP off no jogo | Conhecido | Mensagem "Aguardando posicoes" |
| iframe Overlay tab em file:// | Limitacao | So via `localhost:8088` |
| v2c nao no installer CI remoto | Pendente commit | Gerar novo test build apos commit |

---

## 15. Investigacao Austria .otk (penalidades duplicadas)

**Arquivo:** `Austria_20260628_212502_32779D.otk`  
**Sintoma:** Site mostrava `numPenalties` e `penaltiesTimeSec` dobrados.

**Conclusao:** Origem no **arquivo `.otk`**, nao no frontend.

**Causa raiz:** Jogo re-emite historico de eventos `PENA` ao fim de sessao ou apos reconnects; `LeagueFinalizer` agrega sem dedup logico suficiente.

**Hipoteses Wagner confirmadas plausiveis:** multiplas quedas de lobby + retorno no fim da corrida amplificam duplicacao.

**Acao:** Fix adiado v1.1.48. Live UI ja deduplica contagem para display. Coletar mais `.otk` (My Team, Sprint, 100%) antes do pacote grande.

---

## 16. Preview local sem SimHub

Scripts em `/tmp/overtake-preview/` (nao versionados no repo):

```bash
# Gerar preview dashboard
python3 /tmp/overtake-preview/build.py
open /tmp/overtake-preview/race-ui-preview.html

# Gerar preview overlays (usar hash para trocar view)
python3 /tmp/overtake-preview/build_overlays.py
open /tmp/overtake-preview/overlays-preview.html#weather
open /tmp/overtake-preview/overlays-preview.html#gaps
open /tmp/overtake-preview/overlays-preview.html#battle
```

O mock injeta `FakeWebSocket` com dados sinteticos (qualy/corrida alternando).

---

## 17. Como retomar numa nova conversa

1. **Ler este arquivo:** `docs/LIVE-RACE-UI-HANDOFF.md`
2. **Ler cloud (se for fase 2):** `docs/LIVE-CLOUD-BROADCAST-DESIGN.md`
3. **Checkout branch:**
   ```bash
   cd /Users/wagner.pereira/Documents/Plugin2.0/overtake-simhub-plugin
   git fetch --all --prune
   git checkout feat/race-ui-test-build
   git pull
   git status   # verificar se doc e v2c estao commitados
   ```
4. **Testar:** preview local ou installer CI
5. **Continuar backlog** secao 13 conforme feedback corrida real

### Prompt sugerido para nova sessao Cursor

```
Estou continuando a Live Race UI do plugin Overtake.
Leia docs/LIVE-RACE-UI-HANDOFF.md e docs/LIVE-CLOUD-BROADCAST-DESIGN.md.
Branch: feat/race-ui-test-build.
Quero [descrever tarefa].
```

---

*Ultima atualizacao: 30/Jun/2026 — Wagner / sessao Cursor Agent.*
