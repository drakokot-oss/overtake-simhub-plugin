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

Cinco camadas independentes de filtragem (defense-in-depth) removem entradas que não são pilotos reais. **Princípio fundamental (v1.1.30): evidência positiva primeiro.** Antes de aplicar qualquer heurística de phantom (overflow, AI flag, generic tag), todos os filtros checam se há evidência positiva de que o slot pertence a um piloto real. Se sim, NUNCA filtra.

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
