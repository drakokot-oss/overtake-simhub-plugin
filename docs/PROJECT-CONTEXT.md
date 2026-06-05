# Project Context — Overtake Telemetry SimHub Plugin

Referência técnica interna para desenvolvimento e manutenção do plugin.

---

## Arquitetura — Pipeline de dados

```
F1 25 Game  →  UDP Packets  →  UdpReceiver  →  PacketParser  →  SessionStore  →  LeagueFinalizer  →  OtkWriter
   (game)      (port 20778)    (queue)          (dispatch)       (accumulate)     (transform)         (.otk file)
```

| Componente | Arquivo | Responsabilidade |
|------------|---------|-----------------|
| **UdpReceiver** | `UdpReceiver.cs` | Escuta UDP na porta configurada; forward opcional para SimHub (20777) |
| **PacketParser** | `Parsers/PacketParser.cs` | Despacha bytes brutos → objetos tipados por `packetId` (1–11) |
| **SessionStore** | `Store/SessionStore.cs` | Acumula estado: sessões, drivers, voltas, stints, penalidades, lobby |
| **LeagueFinalizer** | `Finalizer/LeagueFinalizer.cs` | Transforma store → JSON `league-1.0` com dedup, phantom filter, awards |
| **OtkWriter** | `Security/OtkWriter.cs` | Encripta JSON → `.otk` (AES-256-CBC + HMAC-SHA256) |
| **KeyStore** | `Security/KeyStore.cs` | Chaves AES/HMAC ofuscadas via XOR split |

---

## Pacotes F1 25 UDP processados

| ID | Classe | Conteúdo | Frequência |
|----|--------|----------|------------|
| 1 | `SessionData` | Tipo de sessão, clima, track, safety car, SC deploys | Cada frame |
| 2 | `LapDataEntry` | Posição, volta atual, pit stops, grid, status (22 carros) | Cada frame |
| 3 | `EventData` | SSTA, SEND, PENA, COLL, LGOT, OVTK, RTMT, SCAR, RDFL | Evento |
| 4 | `ParticipantsData` | Nomes, equipes, raceNumber, myTeam, platform (22 entries) | ~5s |
| 7 | `CarStatusData` | Assists, fuel, mix | Cada frame |
| 8 | `FinalClassificationData` | Classificação final oficial (posição, voltas, tempo, pneus) | Fim sessão |
| 9 | `LobbyInfoData` | Nomes reais do lobby (antes da sessão) | No lobby |
| 10 | `CarDamageEntry` | Desgaste de pneus, danos aerodinâmicos (22 carros) | Cada frame |
| 11 | `SessionHistoryData` | Melhores tempos, histórico de voltas por driver | ~1s/driver |

---

## Resolução de nomes

O F1 25 tem vários cenários que afetam como os nomes dos pilotos aparecem na telemetria:

### Fluxo de resolução

```
LobbyInfo (packet 9)  →  _lobbyNameByTeamRn["raceNumber_teamId"]
                          _lobbyNameByTeamOnly[teamId]  (fallback unambíguo)

Participants (packet 4)  →  tags["carIdx"] + entryReliability
                            → Se reliable: _bestKnownTags["raceNumber_teamId"]
                            → Se genérico: ResolveLobbyName(rn, tid)

Finalização:
  ApplyFullMyTeamLobbyMergeIfNeeded()  (lobby vence em full MyTeam)
  RetroResolveNames()                   (bestKnownTags → sessões anteriores)
```

### Cenários tratados

| Cenário | Sintoma | Solução |
|---------|---------|---------|
| `showOnlineNames=OFF` | Tags genéricas `Driver_X` | `ResolveLobbyName` via `_bestKnownTags` / `_lobbyNameByTeamRn` |
| Piloto entra mid-session com telemetria off | Nome AI (HAMILTON, LECLERC...) no Qualifying | `RetroResolveNames` resolve AI roster names via `bestKnownTags` da Race |
| Full My Team lobby | Tags de Participants conflitam com lobby | `_captureFullMyTeam`: lobby "owns" seats; merge no export |
| Cross-session carry-over | Driver muda de Quali para Race | `TagsByCarIdx` carry-over; `bestKnownTags` persiste cross-session |
| Reconexão (carIdx muda) | Fantasma no carIdx antigo | `RemovePhantomDuplicateSeats` remove generic 0-lap cujo seat já pertence a outro |

### Chave de identidade

A identidade de um "seat" no lobby é `raceNumber_teamId`. O jogo garante unicidade por lobby. Esta chave é usada em:
- `_bestKnownTags`, `_lobbyNameByTeamRn` (resolução)
- `RetroResolveNames` (resolução retroativa)
- `RemovePhantomDuplicateSeats` (filtragem de fantasmas)
- `DeduplicateDrivers` usa `teamId_raceNumber_carIdx` (inclui carIdx para não fundir MyTeam)

---

## Final Classification (FC) e Fallback

### FC autoritativo (caminho normal)

O packet 8 (FC) é a fonte autoritativa de resultados — contém posições oficiais, voltas, tempos e pneus definidos pelo jogo.

### Quando FC não chega

O FC pode não ser recebido em:
- **Modo espectador** quando a tela de resultados não carrega (bug do jogo)
- **Desconexão** antes da tela de resultados
- **Perda de pacote UDP** (raro mas possível)

Nesses casos, o auto-export usa fallback de 60s após SEND.

### BuildRaceFallbackResults

Reconstrói resultados a partir da telemetria:

```
1. Calcula maxLaps = max(EffectiveLapCount) de todos os drivers
2. Para cada driver:
   - nLaps = EffectiveLapCount(dr)  ← max(Laps.Count, max(LapNumber), LastRecordedLapNumber)
   - totalMs = soma de lap times disponíveis
   - isRetired = nLaps < maxLaps - 1
3. Ordena: nLaps DESC → totalMs ASC
4. Atribui posições 1..N
```

**Limitações do fallback:**
- Posições são aproximações (baseadas em voltas + tempo, não posição oficial)
- Penalidades de tempo pós-corrida não afetam a ordem
- Tempo total pode ser impreciso se voltas foram perdidas na telemetria (gaps)
- `EffectiveLapCount` mitiga o problema de gaps usando o maior LapNumber

### Diagnósticos

| Campo | Significado |
|-------|-------------|
| `fcRowsPositionGt0` | Linhas FC com Position > 0 |
| `fcMissingForRace` | `true` se Race sem FC |
| `resultSource` | `final_classification` / `fallback_telemetry` / `none` |

---

## Filtragem de pilotos fantasma

Seis camadas independentes de filtragem (defense-in-depth) removem entradas que não são pilotos reais. **Princípio fundamental (v1.1.30): evidência positiva primeiro.** Antes de aplicar qualquer heurística de phantom (overflow, AI flag, generic tag), todos os filtros checam se há evidência positiva de que o slot pertence a um piloto real. Se sim, NUNCA filtra. **Refinamento v1.1.32: sticky-evidence vs evidência atual.** O `HumanCarIdxs[i]` é "sticky" (latched true forever na sessão), mas o F1 25 envia em pacotes Participants iniciais flags `AiControlled=false` errados para slots IA, latcheando o flag indevidamente. A partir da v1.1.32, sticky-human só vence se houver corroboração: `slot.AiControlled==false` no momento OU pelo menos um `DriverRun` com `Laps.Count>0` para esse `carIdx`. **Refinamento v1.1.33: lookup estrito para decisões de filtro.** O `LookupBestKnownTagForEntry` original cai num fallback final por `_lobbyNameByTeamOnly[tid]` que retorna o "único humano daquele time". Útil para *resolver label* (RetroResolveNames), mas perigoso para *decidir filtro*: um IA grid filler no mesmo time de um humano único herdaria o nome do humano e escaparia. A partir da v1.1.33, `IsKnownRealPlayer` (e o name-recovery do FC main loop) usam `LookupBestKnownTagForEntryStrict`, que consulta apenas net-key + rn-key.

### Conceito-chave: overflow slot

`participantsPeakNumActive` é o pico de `NumActiveCars` (packet 4) durante a sessão. **Slots com `carIdx >= peak` são "overflow"** — o jogo preenche essas posições do array de 22 com placeholders/AI fillers, mas elas nunca foram ocupadas por pilotos reais ativos. Esses slots aparecem no FC (packet 8) com `Position > 0` mas `NumLaps = 0` porque o array do FC sempre tem 22 entradas. **Exceção (v1.1.30):** um humano que entrou no lobby, apareceu na quali, abandonou no início da race e cujo slot caiu no range overflow ainda é REAL. O filtro deve preservá-lo via lobby/bestKnown.

### Evidência positiva — o que protege um slot de ser filtrado

Em qualquer filtro de phantom, o slot é considerado um piloto real (e portanto **nunca** filtrado) se QUALQUER UM dos seguintes for verdadeiro:

1. `dr.Laps.Count > 0` (telemetria capturou voltas)
2. `sess.HumanCarIdxs[carIdx] == true` (já foi confirmado humano nesta sessão via Participants packet com `AiControlled=false` + `Platform != 255`)
3. `lobbyNameMap[rn_tid]` retorna nome não-genérico (jogador presente no lobby)
4. `bestKnownTags[rn_tid]` retorna nome não-genérico (jogador conhecido em alguma sessão da captura)
5. `bestKnownTagsByNet[netId_tid]` retorna nome não-genérico (jogador conhecido por network-id, robusto a colisões de raceNumber em Custom MyTeam)

`SessionStore.LookupBestKnownTagForEntry(slot)` consulta 3, 4 e 5 com a prioridade correta (net-key → rn-key se não-ambígua → teamId-only). `LeagueFinalizer.IsKnownRealPlayer(sess, store, carIdx, slot)` combina 2 + lookup acima.

### 1. ResolveNamesFromLobby (durante Participants ingestion) — `SessionStore`

- Resolve nome via `ResolveLobbyName(team)` PRIMEIRO; se conseguir nome real, slot é preservado mesmo com AI flag ou em overflow
- Skip apenas quando: `team.AiControlled` + `!hasKnownName` + `!confirmedHuman`
- Skip placeholder em overflow apenas se `!wasHuman` E `!hasKnownName`

### 2. IngestFinalClassification main loop (durante FC ingestion) — `SessionStore`

- Skip overflow + 0 laps apenas se `!confirmedHuman` E `!hasKnownName` (lobby lookup)

### 3. IngestFinalClassification post-FC registration (após FC) — `SessionStore`

- Skip overflow apenas se `!wasHuman` E `!hasKnownName`

### 4. IsPhantomEntry / RemovePhantomDrivers (pré-finalização) — `LeagueFinalizer`

Remove de `sess.Drivers` antes do processamento FC:
- Drivers com `Laps.Count > 0` **nunca** são filtrados
- **Guard `IsKnownRealPlayer` aplicado primeiro** (online): se conhecido em lobby/bestKnown ou wasHuman, preserva
- AI-controlled + 0 laps = grid filler (filtra)
- Generic tag + 0 laps + sem team válido = slot vazio (filtra)
- **Online:** generic + 0 laps (sem evidência positiva) → phantom (cobre tanto overflow quanto AI flag stale dentro do range ativo)

### 5. ShouldSkipFcAiGridFillerRow (durante FC loop) — `LeagueFinalizer`

Filtra linhas FC que não representam pilotos reais:
- Pula se Math.Max(fcLaps, telemLaps) > 0
- Pula se `confirmedHuman` (HumanCarIdxs)
- **Guard `IsKnownRealPlayer` aplicado primeiro** (online): se conhecido em lobby/bestKnown, preserva
- **Online (sem evidência positiva):** generic + 0 laps + 0 bestLap → filtra (cobre overflow E within-range com AI stale)
- **Offline:** AI-controlled ou roster heuristic + 0 laps

### 6. RemovePhantomDuplicateSeats (pós-RetroResolve) — `LeagueFinalizer`

Remove fantasmas de reconexão:
- Generic tag + 0 laps + `raceNumber_teamId` já pertence a outro driver com nome real
- Só em sessões online

### 7. ApplyResultsPostFilter — Camada 6 (v1.1.32) — `LeagueFinalizer`

Última linha de defesa, executada no final do `FinalizeSession` sobre o `resultsOut` já construído (depois de FC main loop, fallbacks, e antes do re-numbering de posições):
- Drop apenas se TODOS verdadeiros simultaneamente: `numLaps==0` + tag genérica + `slot.AiControlled==true` + sem evidência positiva (`IsKnownRealPlayer` retorna false sob a regra v1.1.32)
- Cada drop emite uma nota `[CAMADA-6]` em `_debug.notes` para rastreabilidade
- Só atua em sessões online
- **Cobre o caso Monaco_20260510 ci=19**: AI grid filler que escapou todos os filtros upstream porque `HumanCarIdxs[19]` foi latcheado por pacote Participants inicial bugado

### Segurança (invariantes)

- Pilotos com nome real (não genérico) **nunca** são filtrados
- Pilotos com `HumanCarIdxs[carIdx] = true` **nunca** são filtrados
- Pilotos com `Laps.Count > 0` **nunca** são filtrados
- Pilotos com `(rn, tid)` em **`lobbyNameMap`**, **`bestKnownTags`** ou **`bestKnownTagsByNet`** (network-id) **nunca** são filtrados — incluindo ABANDONOS no início da race (v1.1.30)
- Sessões offline (`NetworkGame == 0`) não são afetadas pelos filtros adicionados em v1.1.29/v1.1.30

### Limitação conhecida: showOnlineNames=OFF + lobby também sem nome real

Quando um jogador real liga o jogo com `showOnlineNames=OFF` E o lobby do F1 25 também recebeu o nome dele como `"Player"` (genérico — comportamento aleatório do jogo), o plugin não tem como inferir o nome real desse jogador. Ele aparecerá como `Driver_X` ou `Car_X` nos resultados, mas o slot é PRESERVADO porque tem laps válidas. Isso NÃO é um phantom — é um jogador real sem identificação visível.

---

## Full My Team lobbies

### Detecção

`DetectFullMyTeamGrid`: todos os slots ativos com `MyTeam=true` + `TeamId!=255` + online.
Requer 2 pacotes Participants consecutivos (streak ≥ 2) para ativar `_captureFullMyTeam`.

### Comportamento

- Lobby "owns" o seat: Participants não sobrescreve `_bestKnownTags` quando lobby já tem nome
- Export merge: `ApplyFullMyTeamLobbyMergeIfNeeded` força lobby names em `bestKnownTags`
- `teamName` = `"MyTeam"` para todos os carros custom
- `DeduplicateDrivers` usa `carIdx` na chave para não fundir dois MyTeam humanos no mesmo squad

### Nota

`_captureFullMyTeam` é latched (nunca volta a false durante a captura). No F1 25, lobbies são sempre 100% My Team ou 100% equipes reais — não há mistura.

---

## Auto-export timing

```
SEND (fim sessão Race)
  ├─ FC já recebido → _autoExportArmed = true → export em 5s
  ├─ FC chega depois → fcStable timer → export em 5s após FC
  └─ FC nunca chega → fallbackElapsed → export em 60s após SEND
```

**SSTA (nova sessão)** reseta flags se export não foi armado, prevenindo export duplicado.

`IsTerminalSession` (a partir da v1.1.31) confia exclusivamente em `Lookups.SessionType[id] == "Race"`:
- `id=13 "Sprint"` → não terminal (espera-se a Main Race em seguida)
- `id=10/11/15/16/19/25/26/29/30/36 "Race"` → terminal
- A heurística antiga `hasSprintShootout && raceCount <= 1` foi removida porque quebrava em fins de semana com SprintShootout sem SprintRace (Baku 2026-05-07).

---

## Auto-rotação de captura (v1.1.31)

Para impedir que duas corridas/eventos diferentes caiam no mesmo `.otk`, a captura é fechada automaticamente em 3 momentos independentes (defesa em camadas):

### Camada 1 — troca de pista (`SessionStore.AutoRotateRequested`)

No início de `Ingest`, antes de criar qualquer `SessionRun` para o pacote novo:

```
SE pacote = Session (id=1)
   E parsed.Session.TrackId != _lastTrackId
   E HasClosedTerminalSession() == true (já existe Race com FC na captura atual)
ENTÃO
   AutoRotateRequested = true
   AutoRotateReason = "trackId X->Y after closed race"
   RETURN  (pacote é descartado intencionalmente)
```

`OvertakePlugin.DataUpdate` consulta a flag depois de drenar a fila e:
1. Chama `TryAutoExport()` se `AutoExportJson=on`
2. Chama `BeginNewCaptureSession()` (limpa store + flags)
3. Chama `_store.ClearAutoRotateRequest()`

O próximo pacote do novo evento é ingerido em uma captura fresh.

### Camada 2 — após auto-export (`OvertakeSettings.AutoCleanAfterExport`)

Após cada `TryAutoExport()` que retorne `true`, se `AutoCleanAfterExport=on` (default), `BeginNewCaptureSession()` é chamado imediatamente. Cobre o cenário "narrador transmite Baku Race → Quali Monaco" sem clicar em "Nova sessão".

`SettingsSchemaVersion` em `OvertakeSettings` migra silenciosamente usuários da v1.1.30 para `AutoCleanAfterExport=true` no primeiro launch da v1.1.31.

### Camada 5 — defesa em profundidade no Finalizer (`LeagueFinalizer.ApplyMultiTrackGuard`)

Se Camadas 1 e 2 falharem (ex.: `AutoExportJson=off` E `AutoCleanAfterExport=off`), `Finalize` detecta `Sessions[]` com 2+ trackIds distintos e descarta tudo exceto o trackId do `LastPacketMs` mais recente. Adiciona uma nota `[POST-HOC] Multi-track capture detected ...` em `_debug.notes`. **O `.otk` final NUNCA contém dois eventos.**

### Quando NÃO rotaciona

- `_lastTrackId` ainda é null (primeira sessão da captura)
- `trackId` igual ao anterior (Practice → Quali → Race do mesmo fim de semana)
- `HasClosedTerminalSession()` retorna false (nenhuma Race com FC ainda)
- Camada 5 só ativa se houver 2+ trackIds **e** `LastPacketMs` permitir desempate

---

## Modo espectador

### O que funciona normalmente
- Participants (22 carros), LapData (22 carros), Events (broadcast), CarDamage, CarStatus

### Limitações conhecidas
- `playerCarIdx = 255` (não há carro do jogador)
- `CarPosition` e `GridPosition` podem ser 0 para carros fora da câmera
- `SessionHistory` (packet 11) vem apenas para o carro em foco — voltas podem ter gaps
- FC (packet 8) pode **não ser enviado** se a tela de resultados não carregar
- `numActive` em Participants pode ser menor que o total real

### Mitigações
- `EffectiveLapCount`: usa max(Count, maxLapNumber, LastRecordedLapNumber) para gaps
- Fallback 60s: garante export mesmo sem FC
- Early registration: cria DriverRun placeholders para carros detectados em LapData
- Cross-session carry-over: preserva nomes entre Quali → Race

---

## Formato .otk

Layout binário:

```
[4 bytes]  Magic "OTK1"
[2 bytes]  Version (uint16 LE, atualmente 1)
[16 bytes] AES-CBC IV (random)
[4 bytes]  Ciphertext length (uint32 LE)
[N bytes]  Ciphertext (AES-256-CBC, PKCS7 padding)
[32 bytes] HMAC-SHA256 (sobre tudo acima)
```

Chaves: `KeyStore` armazena via XOR split (`partA ^ mask`). Após ConfuserEx, ficam opacas no binário.

---

## Release process

```powershell
# Teste local (build + test + package)
.\scripts\Build-Package.ps1

# Release completo (bump + build + test + git + GitHub Release)
.\scripts\Release.ps1 -Version "X.Y.Z"

# Release sem push (teste local)
.\scripts\Release.ps1 -Version "X.Y.Z" -NoPush
```

Detalhes em [RELEASE-PROCESS.md](RELEASE-PROCESS.md).

### Arquivos de versão

| Arquivo | Campo |
|---------|-------|
| `Properties/AssemblyInfo.cs` | `AssemblyVersion`, `AssemblyFileVersion` |
| `CHANGELOG.md` | Entrada `## [X.Y.Z]` com notas |
| `version.json` | `version`, `released`, `releaseNotes`, `installerUrl` |

---

## Problemas conhecidos resolvidos

### v1.1.41

| Demanda | Como foi feito |
|----------|---------------|
| **Fechar o suporte ao formato UDP 2026 e validar em produção (humanos + grid completo).** A v1.1.40 leu o núcleo (Participants/CarStatus) mas mantinha 2026 como experimental, faltando LobbyInfo e validação em humanos. | **Validação:** 4 capturas em UDP 2026/v1.1.40 — Austria offline (grid 22, **11/11 equipes corretas** incl. Cadillac/Audi/Hadjar/Bortoleto/Lindblad, ERS 22/22 com storePctAvg coerente) + 3 lobbies online (Brazil/Silverstone/Spa) com humanos (times/plataformas corretos; nomes ocultos viram placeholder, como no F1 25). **LobbyInfo 2026:** parser roteado por formato (stride 42→43, platform 3→4, name 4→5, carNumber 36→37; teamId@1 inalterado), confirmado por ground-truth (ERT Drako%: teamId 228, Steam, carNum 73). **lobbySettings profundo OMITIDO no 2026:** os offsets @639+ deslocaram por valor não-fixável (a amostra de 1ª ocorrência os traz zerados), então `GameInfo.AreDeepSessionFieldsMapped` (só 2025) faz o store pular esses campos → `lobbySettings` sai null em vez de lixo coincidente (VSC/red-flag do Session idem; `safetyCar.fullDeploys` segue vindo de eventos). **2026 promovido a `SupportedParseFormats`** → flag `unsupportedUdpFormat` e amostragem raw param de disparar. Tests 34 (atualizado), 38 (LobbyInfo real bytes), 39 (lobbySettings omitido no 2026). |

**Princípio de design reforçado (v1.1.41):**
- **Omitir > adivinhar.** O bloco profundo do Session não era mapeável com confiança, então é omitido (null) explicitamente no 2026 — melhor um campo ausente e honesto do que um valor coincidente que engana a liga (anti-cheat depende de assists corretos).
- **Validar em produção antes de declarar suporte.** Só promovemos 2026 a "suportado" depois de bater 11/11 equipes num grid real + lobbies com humanos. O ciclo "lança núcleo experimental → usuário captura → valida → promove" evitou declarar suporte cedo demais.
- **Ground-truth é rei.** Cada offset do LobbyInfo foi ancorado num valor conhecido (o próprio gamertag/numero do usuário), não em suposição.

### v1.1.40

| Demanda | Como foi feito |
|----------|---------------|
| **Fase 2 — fazer o plugin LER o formato UDP 2026 (não só detectar/sinalizar).** A v1.1.39 detectava o formato 2026 e capturava amostras cruas, mas não parseava (dados embaralhados). Com o mapa de offsets pronto (engenharia reversa da captura `Spa_20260604_195534` via `_debug.rawSamples`), implementamos o núcleo. | **Parsers format-aware roteados por `packetFormat` no `Dispatch`.** `ParticipantsData` e `CarStatusEntry` ganharam overload `(byte[], ushort)`; o de 1 arg continua 2025 (compat). **Participants 2026:** stride 57→60, offsets deslocados (teamId@5, myTeam@7, raceNumber@8, nationality@9, name@10, yourTelemetry@42, showOnlineNames@43, platform@46) — corrige nomes/equipes. **CarStatus 2026:** stride 55→59 (offsets ERS idênticos) — corrige bateria. **LapData/FC/CarDamage/Event + núcleo do Session:** confirmado layout idêntico (só 22→24 carros, já suportado). `RawSampleHexCap` 256→2048 para a próxima captura cobrir o pacote inteiro (Session profundo @639+ e LobbyInfo). Tests 36/37 validam os parsers 2026 contra os **bytes reais** da captura (NORRIS→McLaren 228, ALONSO→Aston 224, SAINZ→Williams 223; ERS store 4 MJ com stride 59 e lixo com 55). **Decisão consciente:** 2026 permanece fora de `SupportedParseFormats` (continua marcado `unsupportedUdpFormat` + capturando amostras) até validar em captura online com humanos e mapear Session-profundo + LobbyInfo — então a liga usa **UDP Format 2025** para corridas valendo. |

**Princípio de design reforçado (v1.1.40):**
- **Parsers parametrizados por layout, roteados por formato.** Em vez de duplicar parsers inteiros, o layout (stride + offsets) vira dado selecionado pelo `packetFormat`. Adicionar um formato futuro é adicionar uma `Layout`, não reescrever o parser.
- **Validar com bytes reais.** Os Tests 36/37 usam o hex cru capturado em campo como fixture — regressão contra dados de verdade, não sintéticos. O `_debug.rawSamples` (Fase 2 enabler da v1.1.39) virou também a fonte das fixtures de teste.
- **Lançar o núcleo cedo para validar, sem declarar suporte total.** O parsing do núcleo já roda (nomes/equipes/ERS corretos no `.otk`), mas o formato continua "experimental" até a validação em humanos + campos secundários. Isso transforma o próprio teste do usuário no mecanismo de validação, sem arriscar dados sujos em produção.

### v1.1.39

| Demanda | Como foi feito |
|----------|---------------|
| **DLC "F1 25 2026 Season Pack" (conteúdo do F1 26) trouxe equipes/pista novas que apareciam como `Team(220)`...`Team(230)` e `Track(42)` no site.** O usuário rodou lobbies com os 11 times de 2026 (Audi, Cadillac) e o circuito de Madri. Como esses IDs são novos e não estavam em `Lookups`, o plugin caía no fallback `Team(<id>)`. **Bloqueio:** a EA não publicou apêndice oficial de team/track IDs do F1 26; faixa 220–230 e track 42 não existem em spec pública. | **Mapeamento confirmado empiricamente** por capturas rotuladas: a tela de resultados de Suzuka (player→equipe) cruzada com os teamIds dos arquivos deu match em 10/11 sem conflito, e uma corrida vs IA em Monza re-confirmou via pilotos reais (Bortoleto=229→Audi, Bottas=230→Cadillac, etc.). Ordem = F1 25 +220, Sauber→Audi (229), Cadillac nova (230). `225=Alpine` travado por eliminação + arquivo de Madrid. `Lookups.Teams` += 220–230 (nomes da tela 2026), `Lookups.Tracks` += `42=Madring`. **Detecção de conteúdo 2026 independente do formato UDP:** `teamId∈[220,230]` ou `track==42` → `game="F1_26"` mesmo com `packetFormat=2025` (o pacote roda dentro do F1 25 e por padrão emite formato 2025). Novos campos `_debug.game`: `formatLabel`, `contentPack2026`. Tests 31–33. |
| **Descoberta crítica: a opção "UDP Format 2026" do jogo quebra o parser.** Experimento controlado (Monza/Áustria em UDP 2025, Brazil em UDP 2026, mesma v1.1.38): em formato 2026 os corpos de Participants (nomes/teamId viram lixo: 0,1,25,255) e CarStatus (ERS = 1.9×10³²) leem offsets errados, e surge um packetId 16 novo (por frame). Em formato 2025 tudo parseia limpo. | **Proteção + preparação Fase 2.** `GameInfo.IsParseSupportedFormat` declara os formatos suportados (hoje 2025). Formato não-suportado → `_debug.game.unsupportedUdpFormat=<fmt>` + nota, pra nunca confiar silenciosamente em export ilegível. **Coletor de amostras raw:** `_debug.rawSamples` captura 1 amostra por packetId (formato+tamanho+hex até 256B) direto dos bytes crus quando o formato é não-suportado — embute no próprio `.otk` o material pra engenharia reversa do layout 2026, sem ferramenta externa. `ParsedPacket.RawData` carrega os bytes do `Dispatch` ao store. Tests 34–35. |

**Princípio de design reforçado (v1.1.39):**
- **Conteúdo ≠ formato de fio.** "Que jogo/temporada" (conteúdo: equipes, pista, pilotos) é independente de "como os bytes estão no pacote" (formato UDP). O plugin agora trata os dois eixos separadamente: `game`/`contentPack2026` (conteúdo) vs `formatLabel`/`unsupportedUdpFormat` (formato).
- **Degradação graciosa, com evidência embutida.** Em vez de produzir lixo silencioso para o formato 2026, sinalizamos `unsupportedUdpFormat` e capturamos `rawSamples` — o próprio arquivo problemático vira a fonte de dados pra implementar o suporte. Transforma "bug report" em "spec parcial".
- **Preparado para qualquer formato.** `IsParseSupportedFormat` + ponto de extensão documentado em `Dispatch` deixam a Fase 2 (parsers por formato) plugável sem refatorar o caminho 2025 que já funciona.

### v1.1.38

| Demanda | Como foi feito |
|----------|---------------|
| **Fim de semana Sprint gerava múltiplos `.otk` em vez de UM consolidado** (user-reported: "tivemos uma corrida esses dias que nao gerou corretamente um arquivo so com o qualy da sprint, sprint, qualy corrida principal e a corrida principal"). Cross-check com a spec oficial F1 25 (`Data Output from F1 25 v3.pdf`, anexo "Session types") revelou mismatch grave em `Lookups.SessionType`: IDs 10..14 (todas variantes de Sprint Shootout per spec) estavam mapeados como `"Race"`/`"Race2"`/`"TimeTrial"`/`"Sprint"`/`"SprintShootout"`. Como `IsTerminalSession(byte id)` retorna `Lookups.SessionType[id] == "Race"`, isso fazia: (1) Sprint Shootout 1 (id=10) → `IsTerminalSession(10) == true` → auto-export disparava no fim da Quali da Sprint, dividindo o weekend em 2+ arquivos; (2) Sprint Race / Race 2 (id=16, também mapeado como "Race") → mesmo trigger prematuro, separando Sprint Race da Race principal. Test 27 da v1.1.37 (Sprint Format consolidator invariant) **não pegou o bug** porque foi escrito usando os IDs antigos errados (id=14 pra SS, id=13 pra Sprint, id=10 pra Race) — coincidiu com o mapeamento incorreto e passou. | Corrigido `Lookups.SessionType` com IDs spec-aligned (10..14 → SprintShootout1..3/Short/OneShot, 15 → Race, 16 → Race2, 17 → Race3, 18 → TimeTrial). `OvertakePlugin.SessionTypeName` (status panel + log) também realinhado. `IsTerminalSession` continua igual (`name == "Race"`), mas agora corresponde APENAS a id=15 + os ids 19/25/26/29/30/36 observados em lobbies online. Resultado: SS+SQ+Sprint+Quali+Race ficam num único `.otk` end-to-end. Test 27 reescrito com IDs reais (10/8/16/5/15) + asserts incrementais em cada step. Test 28 / Test 29 / Test 30 adicionados: Test 28 (`Test-SprintShootoutId10NotTerminal`) feed direto de `sessionType=10 + FC` e assert `HasClosedTerminalSession() == false` + `sessionType.name == "SprintShootout1"`. Test 29 (`Test-SprintRaceId16NotTerminal`) mesma forma pra id=16/"Race2". Test 30 (`Test-CleanCaptureFullyResetsStoreNoDataLoss`) trava o contrato de limpeza pedido pelo usuário: build Race completa → `Finalize` (assert JSON tem tudo: 1 session, 2 participantes, 2 results) → `BeginNewCapture()` → assert `Sessions.Count == 0` + assert que `Finalize` subsequente retorna `sessions == 0` e `participants == 0` (sem residue pra próxima corrida). Migração dos 9 call-sites pré-existentes de `$sp[6] = 10` (e 1 de `$sp[6] = 16`) pra `$sp[6] = 15`. |

**Princípio de design reforçado (v1.1.38):**
- **Spec é a fonte da verdade.** Mappings inferidos empiricamente sobrevivem até o primeiro caso que viole o ID assumido. A spec da EA é pública e linkada nos comentários do `Lookups.SessionType` — qualquer mudança futura de game version (F1 26) deve ser cross-checked contra a spec atualizada antes de mudar mappings.
- **Tests baseados no mesmo bug que estão protegendo NÃO são tests.** Test 27 da v1.1.37 usou IDs antigos errados pra "validar" o consolidator, e por coincidência passou apesar do bug. v1.1.38 reescreveu pra usar IDs spec-aligned. Lição: testes de regression devem ser construídos a partir da documentação canônica (spec) e não a partir do código que pretendem validar.
- **Contratos de limpeza são tão críticos quanto contratos de export.** Test 30 trava EXPLICITAMENTE que `BeginNewCapture()` é não-destrutivo do ponto de vista do `.otk` (que é serializado por `Finalize` *antes* da limpeza) e que a limpeza é COMPLETA (zero residue pra próxima corrida). Isso codifica o que era apenas convenção implícita no `OvertakePlugin.cs` em uma forma testável.

### v1.1.37

| Demanda | Como foi feito |
|----------|---------------|
| **Sessão fantasma `Track(None)` quebrava upload no Race Hub quando o plugin era iniciado com a tela de resultados de uma corrida anterior aberta** (`SilverstoneReverse_20260518_224341_734A29.otk`). O arquivo continha 3 sessões — uma carry-over `sessionUID=8973625950122264999` com `sessionType=null`, `trackId=null`, `participantsPeakNumActive=0`, **19 drivers `Car_X`** populados puramente pelo stream repetido de `FinalClassification` (cadência ~5s do F1 25 enquanto a results screen fica aberta), seguida pelas sessões reais de Quali (uid 604989693144124789) e Race (uid 17896758819807250985). Site lê `sessions[0].track.name` para validar circuito, vê `Track(None)` e rejeita o upload. Auto-rotation existente (camada 1 da v1.1.31) **não disparava** porque `HasClosedTerminalSession()` exige uma "Race" com `FinalClassification` no store, e a carry-over não tinha `sessionType` populado — logo `_lastTrackId` permaneceu `null` e `CheckLobbyChange` nunca foi chamado pra disparar a rotação. | **Fix em `LeagueFinalizer.Finalize`:** novo branch de filtragem dropa qualquer SessionRun com a tripla `(SessionType == null && TrackId == null && ParticipantsPeakNumActive == 0)` — combinação só possível em carry-over puro de FC (toda sessão real recebe `Session` packet antes de qualquer `FinalClassification`). Filtrar a sessão também limpa os `Car_X` do `participants[]` global (recomputado walking `sessionsOut`). Diagnóstico: contador `_debug.integrity.carryOverSessionsDropped` + linha por filtro em `_debug.notes` (`Dropped FC-only carry-over session uid=... drivers=... events=...`). Test 26 reproduz exatamente o cenário (FC stream em uid=100 sem Session/Participants, depois Quali+Race em uid=200) e valida `sessions.Count == 1`, `participants[]` sem `Car_X`, drivers reais preservados e contador >= 1. **Bônus defensivo:** Test 27 trava por contrato a invariante "Sprint Format consolida em 1 arquivo" — constrói pipeline SS+SQ+Sprint+Quali+Race no mesmo `trackId` e assert que `HasClosedTerminalSession` só retorna `true` após a Race (Sprint não é terminal), zero notas `AUTO-ROTATE` em `Notes` (trackId nunca moveu) e `Finalize` emite todas as 5 sessões consolidadas. Cobre código existente desde v1.1.31 (`IsTerminalSession` + `HasClosedTerminalSession`) que vinha sem teste dedicado. |

**Princípio de design reforçado (v1.1.37):**
- **Defesa em profundidade contra "lixo de bordas".** Auto-rotation cuida de mudança de pista; filtro carry-over cuida de FC orphans que chegam antes da pista estar definida; o filtro legacy `(sessionType=null && drivers=0)` continua cobrindo o caso "store inicial vazio". Cada camada captura um cenário distinto sem overlap perigoso.
- **Travar invariantes existentes com testes antes que regridam.** O consolidador Sprint funcionava desde v1.1.31 mas não tinha teste dedicado — o usuário razoavelmente assumiu que tinha quebrado quando viu 3 sessões no `.otk` (eram 2 reais + 1 carry-over). Test 27 elimina ambiguidade futura: se algum refactor mexer em `IsTerminalSession` ou `HasClosedTerminalSession`, o CI grita.
- **Transparência via `_debug.integrity`.** Filtros silenciosos viram bug-magnets: contar quantas sessões foram dropadas, com uma nota descritiva pra cada uma, permite triagem imediata se algum dia o filtro pegar algo legítimo (improvável dada a tripla específica, mas o seguro morreu de velho).

### v1.1.36

| Demanda | Como foi feito |
|----------|---------------|
| **Preparar o plugin para o F1 26 sem ter o spec UDP em mãos.** Codemasters anunciou o F1 26 como mod do F1 25 (mesma base), com duas mudanças concretas: 11 equipes (Cadillac entra) e Sauber renomeada para Audi. O grid pula de 22 para 24 carros, o que excederia o cap histórico de 22 em 6 parsers e truncaria silenciosamente os carros 23 e 24. | Estratégia "defensiva e adaptável": novo `Packets/GameInfo.cs` centraliza `MaxSupportedCars = 26` (11×2 + 4 wildcards) e o helper `GameNameFromPacketFormat(ushort)`. Todos os 6 parsers per-car (Participants, LobbyInfo, FinalClassification, LapData, CarDamage, CarStatus) usam essa constante; cada loop tem `if (off + EntrySize > data.Length) break;` para tolerar buffers menores. `LapData` e `CarDamage` relaxaram o early-return que exigia grid completo (`< PacketHeader.Size + EntrySize * NumCars` → `< PacketHeader.Size + EntrySize`). `SessionStore.IngestLapData`/`IngestCarDamage` recebem null guards porque trailing slots agora ficam null. Campo `game` do JSON deriva de `PacketHeader.PacketFormat` via `SessionRun.LastPacketFormat`. Novo bloco `_debug.game` expõe os bytes brutos (`packetFormat`, `gameYear`, `resolvedGameLabel`, `parserMaxSupportedCars`) para triagem rápida. UI labels trocadas para "F1 25 / F1 26" onde a instrução é genérica. Sem mapear Cadillac/Audi/novos pilotos em `Lookups` ainda — esse mapping é trivial (~30min) com 1 captura real do F1 26 e fica como TODO documentado. Tests 23, 24, 25 cobrem game label dinâmico + forward-compat com 24 entries + backward compat com 22 entries. |

**Princípio de design reforçado (v1.1.36):**
- **Degradação graciosa sobre adivinhação.** Cadillac sem ID confirmado vira `"Team(10)"` (feio mas correto); inventar mapping e errar gera dados sujos que poluem o histórico de capturas. UX subóptima por 1 release é aceitável.
- **Defesa por parser_size, não por const.** O cap de carros virou um parâmetro central (`GameInfo.MaxSupportedCars`); cada parser confia no tamanho do buffer recebido em vez de exigir formato exato. Adicionar suporte a 28 carros (caso F1 27 expanda mais) vai exigir 1 alteração no `GameInfo` em vez de 6.
- **Sinalização explícita do jogo.** O `_debug.game` permite diagnóstico imediato: se vier um `.otk` com `game = "F1_2030"`, sei que o spec mudou e posso pedir o pacote bruto antes de tocar em qualquer parser.

### v1.1.35

| Demanda | Como foi feito |
|----------|---------------|
| **`harvestedPctAvgPerLap` da v1.1.34 sempre passava de 100%** (validado em `Spa_20260512_234130_F0EB39.otk` real: 19/19 pilotos com valores 116–134%). Lia como bug mesmo sendo dado correto. | O campo somava MGU-K + MGU-H, cada um com cap regulamentar **independente** de 4 MJ/volta = 100% da capacidade. A soma legítimamente excedia 100%. Substituído por dois campos separados: `harvestedMgukPctAvgPerLap` (média do MGU-K, sempre 0..100%) e `harvestedMguhPctAvgPerLap` (média do MGU-H, sempre 0..100%). Arrays per-lap permanecem inalterados (já eram separados desde v1.1.34). Schema continua `league-1.1` — mudança em campo opcional aditivo. |

**Princípio de codificação reforçado (v1.1.35):**
- Quando uma métrica tem múltiplas fontes regulamentadas com cap individual independente, expor cada fonte separadamente. Somar essas fontes em um único campo gera valores acima do "cap aparente" e confunde quem lê o JSON. Quem precisar do total agregado pode somar do lado consumidor.
- O nome do campo deve refletir a fonte/escopo: `harvestedMguk*` é claro que é só MGU-K; `harvestedPct*` (sem qualificador) era ambíguo.

### v1.1.34

| Demanda | Como foi feito |
|----------|---------------|
| **Coletar uso médio de bateria (ERS) por piloto, em percentual, como visto no jogo** | Novo bloco `ersTelemetry` no JSON do piloto (schema `league-1.1`, retrocompatível). `Packets/CarStatusData.cs` agora lê offsets 29–54 (ERS) além dos primeiros 17 bytes (fuel/TC/ABS). `SessionStore.IngestCarStatus(sid, entries, nowMs)` converte Joules → % na fronteira (4 MJ = 100%), detecta virada de volta via queda do contador `ersDeployedThisLap` (drop > 5pp), e mantém média ponderada pelo tempo entre amostras (`storePctSumWeighted / storePctTimeMs`). Amostras com `networkPaused=1` ficam fora de min/max/avg (não enviesam a média durante pausas) mas são contadas em `samplesPaused`. `LeagueFinalizer.BuildDriverDictionary` emite os campos: `storePctFirst/Last/Min/Max/Avg`, `deployedPctPerLap[]` + `deployedPctAvgPerLap`, `harvestedMgukPctPerLap[]`/`harvestedMguhPctPerLap[]` + `harvestedPctAvgPerLap`, `deployModeLast`, `samplesCount`/`samplesPaused`. Bumped `schemaVersion` para `league-1.1`. |

**Métrica para "consumo médio" vs "economia média"** (princípio de produto):
- `deployedPctAvgPerLap` = **consumo médio**: % da capacidade total gasta por volta (estilo agressivo tem valores próximos a 100%).
- `storePctAvg` = **economia média**: % médio da carga ao longo da corrida (estilo guardador tem valores próximos a 80–90%, agressivo próximo a 30–50%).
- Ambas em percentual (0–100), comparáveis entre pilotos do mesmo grid, alinhadas com o HUD do jogo.

**Invariante anti-regressão (Test 22):** ERS é data **aditiva**. `IsKnownRealPlayer`/`IsPhantomEntry`/`ApplyResultsPostFilter` continuam sem tocar nesses campos. Test 22 re-executa o cenário Brazil da v1.1.33 com payload ERS preenchido para a IA grid filler e confirma que ela continua filtrada.

**Princípio de codificação reforçado:**
- Conversão de unidade (J → %) deve ser feita uma única vez na fronteira (`IngestCarStatus`). O resto do pipeline trabalha sempre em percentual. Isso evita o tipo de bug do v1.1.30 (`InvalidCastException` por boxing errado) e mantém o JSON externo consistente.
- `storePctAvg` é média aritmética das amostras não-pausadas. Tentativa inicial usou média ponderada pelo tempo, mas como o `CarStatus` chega a ~10Hz uniforme, a média aritmética é estatisticamente equivalente — e robusta tanto em produção quanto em test harnesses que disparam pacotes em cadência sub-milissegundo. Amostras com `networkPaused=1` são contadas em `samplesPaused` mas não entram no somatório nem em min/max.

### v1.1.33

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| **Carro fantasma persistia em modo espectador mesmo após a v1.1.32** (`Brazil_20260511_215148_531C9D.otk` ci=19 Visa Cash App #30 — `Car_19` na Quali, `Driver_19` na Race, 0 laps em ambas, `participants[]` global=21 com 19 humanos reais). Camada 6 NÃO disparou (não emitiu `[CAMADA-6]` em notes). | `IsKnownRealPlayer` chamava `LookupBestKnownTagForEntry`, que tem 3 níveis de prioridade: net-key → rn-key → `_lobbyNameByTeamOnly[tid]`. O fallback `teamId-only` foi pensado para o cenário raro "humano no lobby com `rn` diferente do reportado em Participants", retornando o único humano daquele time. Mas quando F1 25 adiciona um IA grid filler no MESMO time de um único humano (Drako% era o único Visa Cash App humano, rn=73), o slot ci=19 (rn=30, IA grid filler) buscava `_lobbyNameByTeamOnly[6]` e recebia `"Drako%"` como evidência positiva. `IsKnownRealPlayer` retornava `true`, fazendo a IA escapar de TODAS as 6 camadas (Camada 6 inclusive — ela só atua quando `IsKnownRealPlayer==false`). | (1) Novo `SessionStore.LookupBestKnownTagForEntryStrict(entry)` que consulta apenas net-key + rn-key, sem o fallback `teamId-only`. (2) `IsKnownRealPlayer` migrado para `Strict`. (3) FC main loop name-recovery (`LeagueFinalizer.cs:1171`) também migrado para `Strict` — evita renomear FC row de IA grid filler para o nome do humano único do mesmo time, que poderia roubar a row do humano ou criar duplicata. |

**Validação manual (v1.1.33):**
- `Brazil_20260511_215148_531C9D.otk` analisado: ci=19 (Visa Cash App #30) era IA grid filler com `aiControlled=true` no JSON. `_debug.diagnostics.lobbyInfo.bestKnownTags` não tinha `30_6`, `lobbyNameMap` não tinha `30_6`, mas `lobbyByTeamOnly[6]=Drako%` (único Visa Cash App humano, rn=73). Esse foi o canal de fuga.
- Test 20 reproduz o cenário exato (Hamilton + Drako% + ci=2 IA grid filler em Visa Cash App): valida que ci=2 é filtrado, Drako% real preserved exatamente uma vez, `participants[]` global tem 2 entradas.
- Test 19 (UNAcapeleto, v1.1.32) continua passando — UNAcapeleto tem entrada exata `74_3` em `_bestKnownTags`, então a versão `Strict` resolve normalmente.

**Princípio de codificação (acumulado da v1.1.32 + v1.1.33):**
- Distinguir lookups por **propósito**: lookup para *resolver label* pode usar fallbacks de menor confiança (teamId-only); lookup para *decidir filtro* deve usar apenas chaves únicas-por-slot (network-id, raceNumber+team). Misturar os dois propósitos abre rotas de escape para fantasmas.
- Toda chave de lookup com fallback "best-effort" (não unicidade garantida) deve ter um nome explícito (ex: `*Strict` vs sem sufixo) para que o ponto de uso revele a intenção.

### v1.1.32

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| **Carro fantasma persistente em modo espectador** (`Monaco_20260510_213256_9A0E7F.otk`, ci=19 Williams #23) — AI grid filler aparecia em Quali (`Car_19`, NotClassified, 0 laps) e Race (`Driver_19`, DidNotFinish, 0 laps, grid=21), inflando `participants[]` global de 19 para 21 entradas. Escapou todas as 5 camadas de filtro phantom existentes. | `SessionStore.HumanCarIdxs[i]` é "sticky" (uma vez true, nunca volta a false na sessão — comportamento intencional). O F1 25 envia, em pacotes Participants iniciais, AI grid fillers com `AiControlled=false` + `Platform!=255`, latcheando `HumanCarIdxs[i]=true` para slots que nunca foram humanos. A guard `IsKnownRealPlayer` (v1.1.30) consultava `HumanCarIdxs` PRIMEIRO e retornava `true` cegamente, fazendo a checagem `positive evidence first` afirmar que aquele slot era real. Resultado: short-circuit `return false` em `IsPhantomEntry`, e `if (effAi == true) return true` em `ShouldSkipFcAiGridFillerRow` nunca era atingido para esse slot porque o caminho FC já tinha sido "aprovado". | (1) Hardening em `IsKnownRealPlayer`: evidência forte de lobby/bestKnown checada PRIMEIRO; sticky `HumanCarIdxs` só vence se corroborado por `slot.AiControlled==false` OU por algum `DriverRun` com `Laps.Count>0` para esse `carIdx`. (2) Camada 6 — `ApplyResultsPostFilter` no `resultsOut` final como rede de segurança que descarta linhas com `numLaps==0` + tag genérica + `slot.AiControlled==true` + sem evidência positiva. Cada drop registrado em `_debug.notes` como `[CAMADA-6]`. |
| **Tabela final de classificação ausente para alguns narradores** — em modo espectador, F1 25 às vezes não envia `FinalClassificationData` (tela de resultados não carrega). O fallback de telemetria (`BuildRaceFallbackResults`/`BuildQualiFallbackResults`) gera resultados aproximados, mas o usuário não tinha visibilidade de que aquela tabela era reconstruída e não oficial. | Ausência de diagnóstico explícito para o consumidor do `.otk`. | `NoteIfFinalClassificationMissing` adiciona uma nota `[WARNING]` em `_debug.notes` quando uma sessão Race/Quali termina sem `FinalClassification`, informando o nome da sessão, track e orientando re-verificar o arquivo. **Trigger de auto-export e auto-clean inalterados** (auto-clean ainda só roda após export bem-sucedido — preserva backup quando o arquivo falha em ser escrito, atendendo ao requisito explícito do usuário). |

**Validação manual (v1.1.32):**
- `Monaco_20260510_213256_9A0E7F.otk` analisado: ci=19 (Williams #23) era AI grid filler com `aiControlled=true` no JSON e 0 laps em ambas as sessões. Não estava em `lobbyNameMap` nem em `bestKnownTags`. `HumanCarIdxs[19]` foi latcheado por pacote inicial bugado (única explicação para escape dos filtros existentes). Hipótese confirmada por: tag em Quali (`Car_19` — placeholder do `EarlyRegisterDriver`) vs em Race (`Driver_19` — placeholder do FC main loop sem entrada em `TagsByCarIdx[19]`)
- `Driver_13` (P15, 36 laps, Stake) e `Driver_16` (P4, 39 laps, Mercedes) confirmados como **jogadores reais** com `LobbyInfoData.name="Player"` (privacy on). Mantidos nos resultados pela invariante `Laps.Count > 0 ⇒ never filter`. Não são fantasmas — não devem ser removidos
- Test 18 simula a sequência exata (pacote Participants inicial bugado para ci=2 + correção posterior + FC com 0 laps): valida que ci=2 é filtrado, Hamilton/Verstappen reais preservados, e nota `[CAMADA-6]` aparece em `store.Notes`
- Test 19 simula UNAcapeleto (lobby evidence + slot.AiControlled=true após disconnect): valida que continua preservado como DNF — lobby evidence vence sobre flag IA atual

**Princípio de codificação para evitar regressões similares:**
- Todo flag "sticky" (latched true forever) que sirva de evidência POSITIVA para preservar dados precisa de um mecanismo de invalidação quando outras evidências contradizem o latch. Sticky-true é seguro como "memória do que já foi confirmado", mas perigoso como "fonte única de verdade no momento da decisão" — sempre cruzar com o estado atual (`slot.AiControlled`, `Laps.Count`, etc.).
- Camadas de defesa em profundidade (5 → 6 nesta release) devem cada uma ter sua própria invariante explícita e logging de drops, para que regressões fiquem visíveis no `.otk` exportado e não sejam descobertas só meses depois em produção.

### v1.1.31

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| **Export falhando com `Specified cast is not valid`** (regressão crítica reportada por usuário em produção da v1.1.30) — auto-export silenciosamente falhava no fim da corrida e o botão manual de export mostrava o erro na UI | `LeagueFinalizer.cs` linha 1183 boxava `dr.GridPosition` (campo `byte` em `DriverRun`) diretamente como `object` no fallback de drivers presentes em `sess.Drivers` mas não classificados pelo FC main loop. `ComputeAwards` / "Most Positions Gained" depois fazia `(int)gridObj` em um `Byte` boxed → `InvalidCastException`. Bug latente desde v1.1.12 mas só virou reprodutível com a v1.1.30 porque o "positive evidence first" passou a preservar muito mais pilotos reais que abandonaram cedo (eles caem nesse fallback) | (1) Fix do escritor: `(object)(int)dr.GridPosition` para boxar como `Int32` (paridade com a linha 1127 do FC main loop). (2) Defesa em profundidade nos leitores: `Convert.ToInt32(...)` em `ComputeAwards` e `ComputeMostConsistent` — aceita qualquer `IConvertible` e nunca mais quebra o export inteiro por boxing errado de um único campo numérico |
| Captura cruzando dois eventos no mesmo `.otk` (Monaco_20260507: Baku SS+OSQ+Race + Monaco Quali+Race no mesmo arquivo, 36 participantes globais) | (1) `CheckLobbyChange` só limpava caches de nome quando trackId mudava — nunca dividia a captura. (2) Após auto-export, o store NÃO era limpo. (3) Para um narrador transmitindo várias corridas seguidas, "Nova sessão" raramente é clicado | 4 camadas independentes: Camada 1 (`SessionStore.AutoRotateRequested` + reação no `OvertakePlugin.DataUpdate`), Camada 2 (`OvertakeSettings.AutoCleanAfterExport=true` por padrão), Camada 3 (`IsTerminalSession` simplificado), Camada 5 (`LeagueFinalizer.ApplyMultiTrackGuard` como defesa em profundidade) |
| Auto-export não disparava em fim de semana com SprintShootout sem SprintRace (Baku 2026-05-07: SS → OSQ → Race). Sem export, sem auto-clean, captura ficava aberta para o próximo evento | `IsTerminalSession` exigia `raceCount >= 2` quando havia SprintShootout, partindo do pressuposto inválido de que SprintShootout sempre teria SprintRace seguida de Main Race | Removido o gating por `raceCount`. Agora confia em `Lookups.SessionType[id]`: id=13 "Sprint" não dispara, ids 10/15/16 etc. "Race" disparam — robusto para todas as combinações de fim de semana |

**Validação manual (v1.1.31):**
- Test 15 simula a sequência exata Baku Race + FC → Monaco Quali primeiro pacote: `AutoRotateRequested` levanta, pacote do Monaco é rejeitado, `BeginNewCapture()` limpa store, próxima ingestão cai em captura fresh
- Test 16 simula Camadas 1 e 2 desativadas: 2 sessões com trackIds diferentes chegam ao `Finalize`; `ApplyMultiTrackGuard` mantém só Monaco e emite `[POST-HOC] Multi-track capture detected` em `_debug.notes`
- Test 17 reproduz o byte-boxing cast bug — Hamilton finished + Verstappen DNF (FC `Position=0`, presente em `sess.Drivers` com `GridPosition>0`). Sem o fix, `Finalize` lança `InvalidCastException("Specified cast is not valid.")`. Com o fix, o resultado contém os 2 pilotos, o `grid` do Verstappen é boxed como `Int32`, e `awards.mostPositionsGained` é computado normalmente
- Arquivo `Monaco_20260507_232047_4B9264_FIXED.otk` gerado manualmente (post-hoc filter via Python) confirmou que a estrutura corrigida abre normalmente no site

**Princípio de codificação para evitar regressões similares:**
- Sempre que uma `Dictionary<string, object>` armazenar valores numéricos vindos de `byte`/`uint`/`ushort`, **boxar com cast explícito para `int`** (`(object)(int)x`) — boxing direto de tipos primitivos não-`int` cria armadilhas para qualquer leitor que faça `(int)dictValue`. Fingerprint do bug: `InvalidCastException("Specified cast is not valid.")`
- Quando o leitor não pode garantir o tipo exato (vier de fonte heterogênea), usar `Convert.ToInt32(obj)` — aceita `Byte`/`UInt32`/`Int16`/etc. transparentemente

### v1.1.30

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| Pilotos reais que abandonavam a corrida antes da primeira volta eram filtrados (UNAcapeleto / Las Vegas) | Os filtros de overflow + 0 laps da v1.1.29 só checavam `HumanCarIdxs[carIdx]` da sessão atual. Quando um humano entrava na quali em ci=overflow, abandonava a race com 0 laps e o slot ficava AI-controlled, era filtrado por engano | Adicionado guard `IsKnownRealPlayer(sess, store, carIdx, slot)` em todos os 5 filter points: checa lobbyMap, bestKnownTags e bestKnownTagsByNet (cross-session). Se há evidência positiva, o slot é PRESERVADO |
| Driver_X dentro do range ativo com flag AI stale (Driver_18 LV quali) | `IsPhantomEntry` v1.1.29 só filtrava overflow (carIdx ≥ peak). Slots dentro do range ativo com tag genérica + AI flag stale escapavam | `IsPhantomEntry` expandido: online + generic + 0 laps + `!IsKnownRealPlayer` → phantom (mesmo dentro do range ativo) |

**Validação contra OTKs reais (v1.1.30):**
- LasVegas Quali (peak=19): UNAcapeleto preservado em P11 (lobby tem 74_3); Driver_18 (rn=31, tid=7, AI stale) filtrado
- LasVegas Race (peak=19): UNAcapeleto preservado como DNF (lobby tem 74_3, ci=19 no overflow, 0 laps)
- Miami v1.1.29 SprintShootout (peak=14): Driver_17/Driver_18 filtrados (não estão no lobby, generic, 0 laps)
- Driver_8 / Car_X com laps > 0: sempre preservados (jogador real com showOnlineNames=OFF + lobby também sem nome real, limitação do jogo)

### v1.1.29

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| Driver_18, Driver_19 (e outros) na qualifying online com grid parcial | `AiControlled` ficava stale (`false`) em pacotes posteriores; `ResolveNamesFromLobby` e o post-FC loop em `IngestFinalClassification` registravam placeholders para todos os 22 slots de `TeamByCarIdx` | 5 filtros de defense-in-depth checando `carIdx >= participantsPeakNumActive` em `SessionStore` (3 pontos de ingestion) e `LeagueFinalizer` (`IsPhantomEntry`, `ShouldSkipFcAiGridFillerRow`); todos com guard `wasHuman` |

**Validação contra OTKs reais (v1.1.29):**
- Monaco peak=18, before=20 results → após fix=18 (filtra Driver_18, Driver_19)
- Miami_1 peak=19, before=20 → após fix=19 (filtra Driver_19; mantém WISNER em ci=18)
- Baku peak=16, before=20 → após fix=16 (filtra Driver_16, 17, 18, 19)
- Miami_2 peak=20 (Custom MyTeam) → 20 (sem mudança, grid completo)
- Race sessions: nenhuma alteração; pilotos AI que assumiram lugar de humanos desconectados (KTS-XvenonzinhoX, jeegoomes_, gutierri, KTS-SkiLo no Baku Race) preservados pois têm laps > 0

### v1.1.28

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| Custom MyTeam — colisão de `raceNumber` (issue #1) | EA bug: lobbies de MyTeam customizado podem atribuir mesmo `raceNumber` a 2 jogadores; chave `raceNumber_teamId` era roubada | Prioridade no `m_networkId` (offset 2 do `ParticipantData`) como chave única; mapa `_bestKnownTagsByNet`; `_rnKeyAmbiguous` para skip de rn-key conflitantes |
| AI fillers herdando gamertags reais | Slots controlados por IA inheriam nome via `(raceNumber, teamId)` colisão | AI guard em `IngestParticipants` impede AI de herdar nome de carIdx confirmado humano |

### v1.1.27

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| HAMILTON no lugar de piloto real | `RetroResolveNames` não resolvia nomes AI roster | Expandido para resolver AI names via `bestKnownTags` |
| Driver_16 fantasma (0 voltas, Qualifying) | `ShouldSkipFcAiGridFillerRow` muito permissivo online | Filtro online: generic + 0 laps + 0 bestLap + sem nome |
| Driver_18 duplicado (Race, carIdx reassign) | Sem filtro para seat duplicado por reconexão | `RemovePhantomDuplicateSeats` remove generic 0-lap com seat já owned |
| Vencedor em P11 (espectador, FC não recebido) | `Laps.Count` < real por gaps; fallback ranking errado | `EffectiveLapCount` usa max(Count, maxLapNumber) |
