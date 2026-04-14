# Changelog

All notable changes to the Overtake SimHub Plugin are documented here.

## [1.1.27] - 2026-04-06

### Fixed
- **Qualifying online — nome AI no lugar de jogador real (HAMILTON → UFB PeEmbaixo):** `RetroResolveNames` agora também resolve tags que correspondem a nomes do roster oficial de AI (HAMILTON, LECLERC, etc.) quando `bestKnownTags` contém um gamertag real diferente para o mesmo `raceNumber_teamId`. Cobre o cenário de jogador que entra mid-session com `showOnlineNames=OFF` e herda sobrenome de AI
- **Qualifying/Race online — entradas fantasma com tag genérica e 0 voltas (Driver_16, Driver_18):** `ShouldSkipFcAiGridFillerRow` aprimorado para modo online — slots com tag genérica, 0 voltas, 0 best-lap, sem confirmação humana (`HumanCarIdxs`) e ausentes de `lobbyNameMap`/`bestKnownTags` são agora filtrados como phantom
- **Race online — fantasma duplicado por reassign de carIdx:** novo filtro `RemovePhantomDuplicateSeats` remove entradas genéricas com 0 voltas cujo `raceNumber_teamId` já pertence a outro piloto com nome real (artefato de desconexão/reconexão em slot diferente)
- **Fallback race results — contagem de voltas incorreta em modo espectador:** `BuildRaceFallbackResults` agora usa `EffectiveLapCount` (máximo entre `Laps.Count`, maior `LapNumber` e `LastRecordedLapNumber`) em vez de `Laps.Count` puro. Corrige ranking errado quando a telemetria do espectador perde voltas intermediárias (gaps) e o FC (packet 8) não é recebido
- **Fallback FC-spillover (espectador):** drivers fora do FC que entram via fallback também usam `EffectiveLapCount`

### Added
- Diagnósticos `fcMissingForRace` e `resultSource` no `exportDiagnostics` para identificar quando resultados vieram de fallback por falta de FC

## [1.1.26] - 2026-03-31

### Fixed
- **Qualificação online — P20 `Driver_19` fantasma (lobby com 19 pilotos):** o FC lista 22 slots; índices `carIdx >= NumActiveCars` do **Participants** (pico, só packet 4) com tag genérica e sem volta/tempo são **ignorados**. Export inclui `participantsPeakNumActive` na sessão (paridade Python `session_store` + `league_finalizer`)

## [1.1.25] - 2026-03-31

### Fixed
- **Final Classification (packet 8) — identidade Quali→Race / liga:** recuperação cross-session e resolução por lobby passam a correr também quando o slot já tem tag **genérica** (`Driver_N`, etc.), não só quando a chave falta em `TagsByCarIdx` (paridade com Python `session_store`)
- **Ponte FC → `drivers{}`:** `BestDriverTagForCarIdx` + `MergeFcDriverBucket` — se ainda só há placeholder mas existe bucket com **voltas** (gamertag ou outro nome), alinha `TagsByCarIdx` e move o `DriverRun` (menos `Car17` / stub vazio vs telemetria noutra chave)
- **Online:** fallback FC **`Driver_{carIdx}`** em vez de `Car_{carIdx}`; se ainda vier `Car_{idx}` online, normaliza para **`Driver_{idx}`**

## [1.1.24] - 2026-03-30

### Fixed
- **FC tag duplicate (online):** segundo assento com a mesma `tag` ja nao usa `Driver_{raceNumber}` como fallback — numeros de corrida repetem-se (#2, etc.) e o primeiro piloto a consumir `Driver_11` roubava o **bucket** do `carIdx` 11, gerando **Car11/Car0** sem telemetria enquanto o jogo mostrava DNF/classificados com tempos. Fallback agora e **`Driver_{carIdx}`** e, se preciso, **`Car{carIdx}`** (paridade com `f125` `league_finalizer.py`)

## [1.1.23] - 2026-03-30

### Fixed
- **FC tag `Car{N}` vs `Driver_{N}`:** reconcilia quando o stub `CarN` existe vazio mas `Driver_N` tem telemetria; match por indice no nome quando `DriverRun.CarIdx` vem 0 no store
- **Race `Retired` +1 lap:** F1 UDP marca muitos classificados a uma volta como `Retired` (7); export mantem `status` cru e acrescenta `classifiedLapped` + `classificationLeaderLaps` para o front / ligas alinharem ao ecran do jogo

### Changed
- Awards **mostPositionsGained** e recorte **mostConsistent** incluem linhas com `classifiedLapped`

## [1.1.22] - 2026-03-30

### Fixed
- **Multiplayer online (`networkGame`):** cada linha do Final Classification com `position > 0` passa a ser **sempre** exportada — nao se descarta como IA filler, ghost `Car{N}` com 0 laps, ou generico sem team no blob (late join / DSQ sem volta / espectador)
- **Dedup:** chave fisica agora inclui `carIdx` (`teamId_raceNumber_carIdx`) para nao fundir dois humanos na mesma equipa My Team

### Added
- Campos `carIdx` e `raceNumber` em cada entrada de `results[]`
- Objeto `exportDiagnostics` por sessao (`fcRowsPositionGt0`, `fcRowsEmittedFromLoop`, contadores de drops, `driversMergedByDedup`, etc.)
- Duas linhas FC com a mesma `tag` em online: segunda renomeada para `Driver_{raceNumber}` ou `Car{carIdx}`

### Note
- Paridade com `f125-telemetry-mvp` `league_finalizer.py`; ver `docs/PROJECT-CONTEXT.md` (secao FC autoritario). Testes: lobbies aleatorios + ligas reais.

## [1.1.21] - 2026-03-28

### Added
- Botao **Nova sessao (limpar captura)** nas definicoes: apaga sessoes, pilotos e caches de nomes em memoria; esvazia a fila UDP pendente; **nao** para o listener — use apos export e antes da proxima corrida para nao misturar duas capturas

## [1.1.20] - 2026-03-27

### Fixed
- Grids **full My Team** (online, todos os slots ativos com `myTeam`): o **Lobby** passa a ganhar na chave `raceNumber_teamId` — Participantes com tag "reliable" ja **nao sobrescrevem** `_bestKnownTags` quando o lobby ja definiu nome para esse assento (evita trocar tempos/nomes entre pilotos)
- No export, **merge** final: `lobbyNameMap` sobrepoe `bestKnownTags` em conflito quando `fullMyTeamGrid` (paridade com Python `finalize_league_json`)

### Added
- Diagnostico `fullMyTeamGrid`, `nameKeyConflicts` em `_debug.diagnostics.lobbyInfo` (conflitos lobby vs bestKnown **antes** do merge)

### Note
- Detecao exige **2 pacotes Participantes consecutivos** com grid full My Team; grelhas oficiais (`myTeam=false`) **nao** sao afetadas

## [1.1.19] - 2026-03-04

### Fixed
- Nomes de assento F1 (VERSTAPPEN, PIASTRI, etc.) com 0 voltas voltavam ao JSON quando o FC reidratatava stubs ou `TeamByCarIdx` nao batia — alinhado ao Python `league_finalizer` v1.1.19
- `RemovePhantomDrivers` antes do loop FC + limpeza de `TagsByCarIdx` para tags fantasma
- `ShouldSkipFcAiGridFillerRow`: IA + 0 laps; fallback por sobrenome oficial quando `aiControlled` ausente na slot
- Penalidades fantasma (`UnservedStopGoPenalty` / `UnservedDriveThroughPenalty`) duplicadas pelo jogo em voltas alem de `numLaps` agora sao filtradas; flag `phantomFiltered` forca correcao de `penaltiesTimeSec` mesmo quando menor que o valor do FC
- `bestByTag` agora sempre usa o minimo entre tempos escaneados das voltas e `SessionHistory.bestLapTimeMs` — corrige best lap contaminado por reuso de `carIdx`
- Filtro ghost `Car{N}` reordenado para rodar APOS checagem de `NumLaps > 0` — pilotos reais nao registrados pelo `IngestParticipants` (ex: `showOnlineNames=Off`) nao sao mais filtrados indevidamente
- Pilotos recuperados via FC agora sao adicionados ao mapa `idxToTag`, garantindo resolucao correta de eventos por `carIdx`

### Changed
- Export agora gera somente `.otk` (criptografado AES-256-CBC + HMAC-SHA256) — arquivo `.json` plain nao e mais gerado ao lado
- O site (Edge Function `decrypt-telemetry`) ja decripta `.otk` automaticamente no upload

### Note
- `fuelTelemetry` / CarStatus ja estavam na v1.1.18; sem mudanca funcional aqui

## [1.1.18] - 2026-03-10

### Fixed
- Session type ID 16 (Main Race em sprint weekends) nao estava mapeado no Lookups, gerando "SessionType(16)" no JSON em vez de "Race"
- IsTerminalSession agora reconhece ID 16 como "Race", garantindo auto-export correto no final da Main Race

### Note
- Weekends normais nao sao afetados — apenas sprint weekends usam ID 16

## [1.1.17] - 2026-03-10

### Fixed
- Sprint weekend: auto-export disparava prematuramente apos o Sprint Race (sessionTypeId=15 = "Race"), impedindo captura do Main Qualifying + Main Race no mesmo JSON
- Deteccao de sprint weekend via presenca de sessao SprintShootout no store: so exporta apos a segunda "Race" (Main Race)

### Note
- Weekends normais (sem sprint) nao sao afetados — comportamento identico ao v1.1.16

## [1.1.16] - 2026-03-06

### Fixed
- Carros fantasma (AI grid fillers) apareciam na corrida quando o jogo envia Participants packets iniciais com AiControlled=false para todos os slots, envenenando o HumanCarIdxs sticky
- Removido check wasHuman do phantom filter para entradas com 0 voltas — barreira laps>0 ja protege pilotos reais

### Improved
- Phantom filter simplificado: 0 laps + AI + generic tag = filtrado (sem dependencia de HumanCarIdxs)
- Validado em Melbourne (17 pilotos reais + 3 AI fillers corretamente removidos)

## [1.1.15] - 2026-03-05

### Fixed
- Pilotos reais desapareciam dos resultados em lobbies com showOnlineNames=OFF (placeholder Driver_XX agora registrado para carIdx com teamId valido)
- SessionHistory sobrescrevia voltas acumuladas em modo espectador (agora faz merge: preserva historico + adiciona novas)
- FinalClassification numCars undercount em spectator (parser agora le 22 slots, ignora position=0)
- Filtro Position > maxNumActiveCars descartava pilotos em posicoes altas (removido; phantom filter downstream ja trata)
- IsPhantomEntry filtrava pilotos reais com tag generica e 0 laps quando tinham teamId valido
- FC results loop ignorava pilotos removidos por deduplicacao (agora re-cria driver entry quando presente no FC)
- Nomes de IA (LAWSON, BEARMAN) atribuidos a pilotos humanos quando showOnlineNames=OFF
- TagReliability system muito agressivo bloqueava toda resolucao de nomes (parcialmente revertido)
- AI grid fillers (carros vazios do jogo) com teamId valido passavam no phantom filter

### Added
- Barreira de seguranca absoluta: piloto com 1+ voltas NUNCA e filtrado como phantom
- HumanCarIdxs tracking: carIdx detectado como humano e marcado permanentemente (carry-over entre sessoes)
- wasHuman check no phantom filter: pilotos que desconectaram (AI assumiu) sao protegidos
- FC como fonte autoritativa: re-cria DriverRun para pilotos listados no FC mas ausentes no SessionStore

### Improved
- Phantom filter multi-camada: laps>0 safety → AI+generic+!human → generic+noTeam
- Resolucao de nomes com fallback Driver_XX quando lobby resolution falha completamente
- SessionHistory merge preserva dados acumulados de pacotes anteriores em spectator mode
- Validado end-to-end em 6 lobbies multiplayer (Spa, Catalunya, Austria, Baku, Singapore, Melbourne)

## [1.1.14] - 2026-03-02

### Fixed
- Carros de IA (grid fillers) apareciam nos resultados mesmo com voltas completadas
- Nomes genericos (Car_15) nao eram resolvidos retroativamente com bestKnownTags de sessoes posteriores
- Status "Retired" incorreto em qualifying quando piloto tinha tempo valido (quirk de transicao quali->race)

### Improved
- Filtro de IA agora exclui todo carro aiControlled, independente de numero de voltas
- Resolucao retroativa de nomes: tags genericas substituidas por nomes reais descobertos em sessoes seguintes

## [1.1.13] - 2026-03-02

### Fixed
- Resolucao de nomes entre lobbies: caches limpos ao detectar mudanca de trackId (lobby change)
- Nome de piloto atribuido ao carro errado quando dois pilotos compartilhavam a mesma equipe (lobbyByTeamOnly ambiguity)
- Nomes de sessoes anteriores vazando para lobbies diferentes (cross-lobby name bleeding)
- Lobby settings travando em valores default (zeros) antes de receber dados reais
- Pilotos humanos com showOnlineNames=0 agora aparecem como Driver_X (admin edita no frontend)
- Modo offline: nomes reais restaurados corretamente quando networkGame=0
- Agregacao de penalidades: soma correta de penaltyCount e penaltyTime a partir dos snapshots
- Registro pos-FinalClassification para carros conhecidos sem tag (Driver_X placeholders)

### Improved
- Resolucao centralizada de nomes via ResolveLobbyName com fallback teamId-only
- Deteccao de ambiguidade por equipe (_lobbyTeamKeys) previne atribuicao incorreta
- Carry-over de nomes entre sessoes agora restrito ao mesmo lobby (same trackId)

## [1.1.12] - 2026-02-26

### Fixed
- Auto-export nao disparava quando SEND e SSTA chegavam no mesmo ciclo de processamento
- Entradas fantasma (Car_X, Player) com 0 voltas apareciam na classificacao do Qualifying
- Volta do pit stop exibida com erro de 1 volta (endLap agora e 1-indexed)
- Primeiro lap de pneu novo mostrava desgaste do composto anterior

## [1.1.11] - 2026-02-24

### Added
- Captura de eventos DTSV/SGSV (drive-through e stop-go cumpridos)
- Campo `status` nas penalidades: issued, unserved, served, reminder
- Delay de 45s no auto-export para aguardar FinalClassification estavel

### Fixed
- Penalidades contadas em dobro (10s aparecia como 20s)
- Eventos de colisao (COLL) poluiam a timeline de penalidades

## [1.1.10] - 2026-02-22

### Fixed
- Clique duplo no botao Export gerava dois arquivos JSON identicos
- Debounce de 5 segundos adicionado ao botao de exportacao manual

## [1.1.9] - 2026-02-20

### Fixed
- Pilotos com `showOnlineNames: false` geravam entradas duplicadas
- Resolucao de nomes entre sessoes (Qualifying -> Race) melhorada

## [1.1.8] - 2026-02-18

### Added
- Dados de dano por volta (damagePerLap) e reparos de asa (wingRepairs)
- Timeline de clima (weatherTimeline) com transicoes durante a corrida
- Previsao do tempo do jogo (weatherForecast)

### Fixed
- Pilotos fantasma com teamId=255 filtrados corretamente
- Mapeamento de stints via FinalClassification (posicao -> carIdx)

## [1.1.7] - 2026-02-16

### Added
- Suporte completo a modo espectador (spectator mode)
- Cache de nomes via LobbyInfo (Packet 9) para resolucao pre-sessao
- Deduplicacao de pilotos por (teamId, raceNumber)

## [1.1.6] - 2026-02-14

### Added
- Resultados reconstruidos quando FinalClassification nao chega
- Awards: volta mais rapida, mais consistente, mais posicoes ganhas
- Relay de pacotes UDP para SimHub (plugin nao interfere nos dashboards)

### Fixed
- Safety Car: interpolacao com timestamps reais (LGOT -> CHQF)
