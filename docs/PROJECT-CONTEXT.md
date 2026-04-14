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

Três camadas de filtragem removem entradas que não são pilotos reais:

### 1. IsPhantomEntry (pré-FC)

Remove de `sess.Drivers` antes do processamento FC:
- AI-controlled + 0 laps = grid filler
- Generic tag + 0 laps + sem team válido = slot vazio

### 2. ShouldSkipFcAiGridFillerRow (durante FC)

Filtra linhas FC que não representam pilotos reais:
- **Offline:** AI-controlled ou roster heuristic + 0 laps
- **Online:** Generic tag + 0 laps + 0 bestLap + não confirmado humano + ausente de lobbyMap/bestKnownTags

### 3. RemovePhantomDuplicateSeats (pós-RetroResolve)

Remove fantasmas de reconexão:
- Generic tag + 0 laps + `raceNumber_teamId` já pertence a outro driver com nome real
- Só em sessões online

### Segurança

- Pilotos com nome real (não genérico) **nunca** são filtrados
- Pilotos com `HumanCarIdxs[carIdx] = true` **nunca** são filtrados pelo FC filter
- Pilotos com laps > 0 **nunca** são filtrados
- Pilotos com `raceNumber_teamId` no `lobbyNameMap` ou `bestKnownTags` **nunca** são filtrados pelo FC filter

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

### v1.1.27

| Problema | Causa raiz | Correção |
|----------|-----------|----------|
| HAMILTON no lugar de piloto real | `RetroResolveNames` não resolvia nomes AI roster | Expandido para resolver AI names via `bestKnownTags` |
| Driver_16 fantasma (0 voltas, Qualifying) | `ShouldSkipFcAiGridFillerRow` muito permissivo online | Filtro online: generic + 0 laps + 0 bestLap + sem nome |
| Driver_18 duplicado (Race, carIdx reassign) | Sem filtro para seat duplicado por reconexão | `RemovePhantomDuplicateSeats` remove generic 0-lap com seat já owned |
| Vencedor em P11 (espectador, FC não recebido) | `Laps.Count` < real por gaps; fallback ranking errado | `EffectiveLapCount` usa max(Count, maxLapNumber) |
