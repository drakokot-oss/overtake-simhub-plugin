# Changelog

All notable changes to the Overtake SimHub Plugin are documented here.

## [1.1.33] - 2026-05-12

### Fixed
- **Carro fantasma persistia em modo espectador mesmo após a v1.1.32 (Brazil_20260511_215148_531C9D.otk — ci=19 Visa Cash App #30):** o slot IA grid filler ainda aparecia nos resultados de Quali (`Car_19`, status DidNotFinish, 0 voltas) e Race (`Driver_19`, status DidNotFinish, 0 voltas, grid=21), inflando `participants[]` global de 19 humanos reais para 21. Causa raiz distinta da v1.1.32: `IsKnownRealPlayer` chamava `LookupBestKnownTagForEntry`, que cai num fallback final por `_lobbyNameByTeamOnly[tid]` quando net-key e rn-key falham. Esse fallback foi pensado para o caso raro "humano no lobby com `rn` diferente do que aparece em Participants", retornando o único humano daquele time. Mas quando o F1 25 adiciona um IA grid filler no MESMO time de um único humano (Drako% era o único Visa Cash App humano, rn=73), o slot ci=19 (rn=30, IA) buscava `_lobbyNameByTeamOnly[6]` e recebia `"Drako%"` como evidência positiva — fazendo a IA escapar de TODAS as 6 camadas de filtro (incluindo a Camada 6 introduzida na v1.1.32, que requer `IsKnownRealPlayer==false` para atuar). Fix: novo `LookupBestKnownTagForEntryStrict` em `SessionStore` que consulta apenas net-key + rn-key (sem o fallback `teamId-only`), e migração de `IsKnownRealPlayer` + name-recovery do FC main loop para essa versão estrita

### Added
- **`SessionStore.LookupBestKnownTagForEntryStrict(entry)`**: versão estrita do lookup público. Resolve nome apenas via `_bestKnownTagsByNet[netKey]` (chave network-id, única por jogador) ou `_bestKnownTags[rn_tid]`/`_lobbyNameByTeamRn[rn_tid]` (chave de seat do lobby, única). NÃO consulta `_lobbyNameByTeamOnly`. Use para qualquer decisão de filtro phantom. O lookup não-estrito (`LookupBestKnownTagForEntry`) continua disponível para `RetroResolveNames`, que só atribui label a slots já considerados reais
- Test 20 em `Test-Finalizer.ps1`: regressão Brazil-style. Cenário com Hamilton (Mercedes ci=0), Drako% (único Visa Cash App humano, rn=73, ci=1) e ci=2 IA grid filler no mesmo time (rn=30, ausente do lobby). Valida que (a) Hamilton e Drako% reais são preservados, (b) Drako% aparece EXATAMENTE uma vez (sem duplicação), (c) ci=2 ghost é filtrado mesmo com `_lobbyNameByTeamOnly[6]==Drako%`, (d) `participants[]` global tem exatamente 2 entradas

### Changed
- `LeagueFinalizer.IsKnownRealPlayer` migrada para `LookupBestKnownTagForEntryStrict`. Humanos identificados via net-key ou rn-key continuam preservados normalmente. Humanos que dependiam apenas do fallback `teamId-only` (cenário raro de Participants reportar `rn` diferente do lobby) continuam protegidos por dois caminhos independentes: o sticky `HumanCarIdxs` corroborado introduzido na v1.1.32 e a renomeação retroativa em `RetroResolveNames` (que ainda usa o fallback completo)
- FC main loop name-recovery em `LeagueFinalizer.cs` linha 1171 também migrado para `Strict`: evita que uma FC row de IA grid filler seja renomeada para o nome do humano único do mesmo time, o que poderia (a) "roubar" a row do humano real, ou (b) gerar duplicata Drako%/Drako% no `resultsOut`

### Note
- `RetroResolveNames` permanece usando o lookup não-estrito intencionalmente: o caller já tem o slot mapeado para um carIdx ativo e quer apenas resolver um label legível. A guarda `sess.Drivers.ContainsKey(resolvedName)` evita criação de duplicatas quando o humano já existe sob seu próprio tag (validado no Brazil_20260511 — Drako% real apareceu apenas uma vez)
- Camada 6 e o hardening de `IsKnownRealPlayer` da v1.1.32 continuam ativos. A v1.1.33 fecha apenas a rota de escape adicional via `_lobbyNameByTeamOnly`. Para futuras regressões, qualquer novo uso do lookup em contexto de filtro deve usar a versão `Strict`

## [1.1.32] - 2026-05-10

### Fixed
- **Carro fantasma persistente em modo espectador (Monaco_20260510_213256_9A0E7F.otk — ci=19 Williams #23):** o piloto IA grid filler aparecia nos resultados de Quali (`Car_19`, status `NotClassified`, 0 voltas) e Race (`Driver_19`, status `DidNotFinish`, 0 voltas, grid=21), inflando o `participants[]` global de 19 humanos reais para 21 entradas. Causa raiz: `SessionStore.HumanCarIdxs[i]` é "sticky" (uma vez marcado true, permanece true para sempre na sessão — comentário no código já alertava). O F1 25 envia, em pacotes Participants iniciais, AI grid fillers com `AiControlled=false` + `Platform!=255`, latcheando `HumanCarIdxs[i]=true` para um slot que nunca foi humano. A guard "positive evidence first" introduzida em v1.1.30 (`IsKnownRealPlayer`) confiava cegamente nesse latch e retornava `true`, fazendo o slot escapar de todas as 5 camadas de filtro phantom (`IsPhantomEntry`, `ShouldSkipFcAiGridFillerRow`, `RemovePhantomDrivers` etc.). Fix em duas camadas: (1) hardening em `IsKnownRealPlayer` requer corroboração antes de honrar o sticky-human flag — evidência forte de lobby/bestKnown OU `slot.AiControlled==false` no momento OU qualquer DriverRun com `Laps.Count>0` para esse `carIdx`; (2) Camada 6 (`ApplyResultsPostFilter`) como rede de segurança final no `resultsOut`

### Added
- **Camada 6 — `LeagueFinalizer.ApplyResultsPostFilter` (defesa em profundidade no resultsOut):** rede de segurança final que roda depois de FC main loop + fallbacks, mas antes do re-numbering de posições. Descarta linhas que simultaneamente têm `numLaps==0` + tag genérica + `slot.AiControlled==true` + sem evidência positiva (lobby/bestKnown). Cada drop é registrado em `_debug.notes` como `[CAMADA-6]` para rastreabilidade. **Invariantes preservadas (validadas no Test 19):** jamais filtra pilotos com voltas (`numLaps>0`), jamais filtra pilotos com evidência forte de lobby/bestKnown, e nunca atua em sessões offline (`NetworkGame!=1`)
- **Diagnóstico "Race ended without FinalClassification" (`NoteIfFinalClassificationMissing`):** quando uma sessão Race/Quali termina sem `FinalClassificationData` (cenário típico de espectador F1 25 onde a tela de resultados não carrega), o finalizer adiciona uma nota `[WARNING]` em `_debug.notes` informando o nome da sessão, track, e orientando que os resultados foram reconstruídos via telemetria. Não altera resultados — apenas avisa quem está consumindo o `.otk` que aquela classificação é aproximada (pode merecer verificação manual ou re-export)
- Test 18 em `Test-Finalizer.ps1`: regressão Monaco-style ghost. Constrói cenário de pacote Participants inicial com `AiControlled=false` + `Platform=Steam` para ci=2 (latcheando `HumanCarIdxs[2]=true`), depois corrige para `AiControlled=true` em pacote posterior, e envia FC com row P3 0-laps `NotClassified` para ci=2. Valida que (a) Hamilton e Verstappen reais são preservados, (b) ci=2 é filtrado, (c) `results.Count == 2`, (d) nota `[CAMADA-6]` aparece em `store.Notes`
- Test 19 em `Test-Finalizer.ps1`: invariante UNAcapeleto. Constrói cenário com lobby contendo UNAcapeleto (rn=74, tid=3), pacote Participants reportando `AiControlled=true` para o slot ci=1 (slot promovido a IA após disconnect), FC com 0 laps DNF. Valida que UNAcapeleto continua nos resultados — evidência forte de lobby vence sobre flag de IA atual, preservando o fix v1.1.30

### Changed
- `IsKnownRealPlayer` reorganizada para checar evidência forte (lobby/bestKnown via `LookupBestKnownTagForEntry`) PRIMEIRO. Só depois consulta o sticky `HumanCarIdxs`. Quando o sticky está true MAS o slot está atualmente marcado `AiControlled=true`, exige corroboração adicional via DriverRun com voltas — caso contrário descarta o sticky como artefato de pacote inicial bugado
- **Auto-export trigger e auto-clean permanecem inalterados (`OvertakePlugin`)**: clean ainda ocorre apenas após export confirmadamente bem-sucedido (`exportOk == true`), preservando backup quando o `.otk` falha em ser escrito. Atendendo ao requisito explícito do usuário ("se auto-export disparar em hora errada, não podemos perder a opção de gerar manualmente")

### Note
- **Limitação conhecida (sem mudança em v1.1.32):** jogadores reais cujo `LobbyInfoData` reportou `name="Player"` (privacy on no perfil F1 25) continuam aparecendo como `Driver_X` nos resultados, COM suas voltas e estatísticas completas. NÃO são fantasmas — são jogadores reais sem identificação visível, e sua remoção indevida violaria a invariante `Laps.Count > 0 => never filter`. Validado no Monaco_20260510: `Driver_13` (P15, 36 voltas, Stake) e `Driver_16` (P4, 39 voltas, Mercedes) são pilotos reais e foram corretamente preservados na v1.1.32

## [1.1.31] - 2026-05-09

### Fixed
- **Export falhando com `Specified cast is not valid` (regressão crítica da v1.1.30):** o auto-export silenciosamente falhava no fim da corrida e o botão "Export League JSON" mostrava `Export failed: Specified cast is not valid` na UI. Causa raiz: `LeagueFinalizer.cs` linha 1183 boxava `dr.GridPosition` (campo `byte` em `DriverRun`) diretamente como `object` no fallback "drivers presentes em `sess.Drivers` mas não classificados pelo FC main loop". Esse caminho passou a disparar muito mais a partir da v1.1.30 porque o princípio "positive evidence first" preserva pilotos reais que abandonaram cedo (incluindo o fix do UNAcapeleto). Quando `ComputeAwards` calculava "Most Positions Gained", o `(int)gridObj` tentava unbox um `Byte` para `Int32` e estourava `InvalidCastException`. Bug latente desde a v1.1.12 — só ficou reprodutível agora. Fix em duas camadas: (1) `(object)(int)dr.GridPosition` no escritor para boxar como `Int32`; (2) `Convert.ToInt32(...)` defensivo nos leitores de `ComputeAwards` e `ComputeMostConsistent` para nunca mais quebrar o export inteiro por um único campo numérico boxado de forma errada
- **Captura cruzando dois eventos no mesmo `.otk` (Monaco_20260507 — Baku + Monaco):** quando um narrador transmitia corridas seguidas sem clicar em "Nova sessão" entre elas, o plugin acumulava todas no mesmo arquivo. O OTK final continha 5 sessões de 2 fins de semana diferentes (Baku SS+OSQ+Race + Monaco Quali+Race), com 36 pilotos no `participants[]` global. Agora a captura é dividida automaticamente em 4 camadas independentes
- **Auto-export não disparava em fim de semana com SprintShootout sem SprintRace (ex.: Baku 2026-05-07: SS → OSQ → Race apenas):** `IsTerminalSession` exigia `raceCount >= 2` quando havia SprintShootout na captura, partindo do pressuposto de que sempre haveria Sprint Race seguida de Main Race. Lobbies com formato sprint que não rodam a sprint race ficavam sem trigger de export, agravando a contaminação cross-event. Agora `IsTerminalSession` confia no nome retornado por `Lookups.SessionType` (id=13 "Sprint" não dispara; ids 10/15/16 etc. "Race" disparam) — robusto para todas as combinações de fim de semana

### Added
- **Camada 1 — auto-rotação por troca de pista (`SessionStore.AutoRotateRequested`):** quando um pacote `Session` chega anunciando `trackId` diferente do último visto E a captura atual já tem alguma `Race` com `FinalClassification`, o store recusa o pacote, sinaliza `AutoRotateRequested=true` e o `OvertakePlugin` reage no próximo `DataUpdate` exportando a captura antiga (se `AutoExportJson=on`) e chamando `BeginNewCapture()`. O primeiro pacote do novo evento que sobreviver ao rotate cai numa captura limpa
- **Camada 2 — auto-clean após auto-export (`OvertakeSettings.AutoCleanAfterExport`):** após cada auto-export bem-sucedido, o store é limpo automaticamente para que a próxima corrida comece do zero. Habilitado por padrão (default `true`); pode ser desabilitado via `OvertakeSettings` para usuários power que preferem múltiplos eventos no mesmo arquivo
- **Camada 5 (defesa em profundidade) — `LeagueFinalizer.ApplyMultiTrackGuard`:** se mesmo assim a captura chegar ao Finalize com 2+ trackIds distintos (Camadas 1 e 2 desativadas/falharam), o finalizer descarta tudo exceto o trackId mais recente e adiciona uma nota `[POST-HOC]` em `_debug.notes`. O `.otk` final NUNCA contém dois eventos
- `SessionStore.HasClosedTerminalSession()`: helper público que retorna `true` quando alguma sessão acumulada é Race com FinalClassification (usado pela Camada 1 para diferenciar "captura aberta vs evento fechado")
- `OvertakeSettings.SettingsSchemaVersion`: marker de versão para migração de settings persistidas. Usuários da v1.1.30 são migrados em silêncio para `AutoCleanAfterExport=true` no primeiro launch da v1.1.31
- Test 15 em `Test-Finalizer.ps1`: simula a sequência exata do `Monaco_20260507` (Baku Race + FC → Monaco Quali primeiro pacote) e verifica que `AutoRotateRequested` levanta, o pacote do Monaco é rejeitado, e após `BeginNewCapture()` a próxima ingestão cria um store limpo
- Test 16 em `Test-Finalizer.ps1`: cobre o caso patológico onde Camadas 1 e 2 não dispararam e duas sessões de tracks diferentes chegam ao Finalize. Valida que `ApplyMultiTrackGuard` mantém só Monaco e emite a nota `[POST-HOC] Multi-track capture detected`
- Test 17 em `Test-Finalizer.ps1`: regressão do bug "Specified cast is not valid". Constrói o cenário exato (Hamilton classificado pelo FC + Verstappen DNF com `Position=0` no FC mas presente em `sess.Drivers` com `GridPosition>0`), chama `Finalize` e afirma que (a) não estoura `InvalidCastException`, (b) Verstappen é preservado pelo fallback path, (c) seu `grid` no resultado é `Int32` (não `Byte`), e (d) `awards.mostPositionsGained` é computado com sucesso

### Changed
- `OvertakePlugin.TryAutoExport()` agora retorna `bool` (true = arquivo gerado e gravado, false = falhou). A Camada 2 só limpa o store quando o export retorna `true`, evitando jogar dados fora se o `.otk` falhar em ser escrito
- `OvertakePlugin.IsTerminalSession()` simplificado: removida a checagem `hasSprintShootout && raceCount <= 1` (que falhava no caso Baku SS+OSQ+Race). Lookups já diferencia `id=13 "Sprint"` (não terminal) de `id=10/15/16 "Race"` (terminal) — confiamos no name lookup
- `SessionStore.BeginNewCapture()` agora também chama `ClearAutoRotateRequest()` para limpar o sinal pendente de rotação

### Note
- O fluxo "narrador transmite múltiplas corridas seguidas" agora gera **um `.otk` por evento** automaticamente. Se a transmissão for Baku Race → Monaco Quali → Monaco Race:
  - Camada 2 dispara após Baku Race com FC + SEND → exporta `Baku_*.otk` + limpa store
  - Monaco Quali e Race acumulam em store limpo
  - Camada 2 dispara após Monaco Race com FC + SEND → exporta `Monaco_*.otk` + limpa store
- Se a Camada 2 falhar (ex.: nunca chegou FC para Baku), a Camada 1 ainda pega no momento que o trackId mudar de 20 para 5
- Se ambas falharem, a Camada 5 garante que o arquivo final tem só Monaco
- Manter o foco no "básico funcionando 100% para todos os casos" antes de revisitar o painel UX (próximo projeto)

## [1.1.30] - 2026-05-06

### Fixed
- **Online race — pilotos reais que abandonavam antes da primeira volta eram filtrados (UNAcapeleto / Las Vegas):** os filtros de overflow (carIdx ≥ `participantsPeakNumActive` + 0 laps) introduzidos em v1.1.29 só verificavam `HumanCarIdxs[carIdx]` da sessão atual. Quando um humano entrava na quali, abandonava no início da race e seu slot caía no range overflow com 0 laps, ele era removido por engano. Agora, o filtro também checa `lobbyNameMap` e `bestKnownTags`/`bestKnownTagsByNet` (cross-session): se houver evidência positiva de que o `(rn, tid)` pertence a um humano conhecido, o slot é PRESERVADO como DNF — nunca filtrado
- **Online qualifying — Driver_X dentro do range ativo com flag AI stale (LV quali Driver_18):** `IsPhantomEntry` foi expandido para também filtrar slots online com tag genérica + 0 laps quando NÃO há evidência positiva (lobby/bestKnown/wasHuman), mesmo dentro do range ativo. Cobre o caso onde `AiControlled` foi true em pacotes iniciais e flipou para false depois (stale flag), o que escapava do filtro v1.1.29

### Changed
- `IsPhantomEntry`, `ShouldSkipFcAiGridFillerRow` e os 3 pontos de `SessionStore` (`ResolveNamesFromLobby`, `IngestFinalClassification` main + post-FC loops) agora compartilham o mesmo princípio: **evidência positiva primeiro**. Se o slot tem nome conhecido em lobby/bestKnown ou foi confirmado humano, NUNCA filtra. Só aplica heurísticas de phantom (overflow, AI flag, generic tag) quando não há nenhuma evidência positiva. Elimina falsos positivos em todos os 5 filter points
- Novo helper `IsKnownRealPlayer(sess, store, carIdx, slot)` em `LeagueFinalizer` centraliza a checagem de evidência positiva

### Note
- **`Driver_X` / `Car_X` que ainda aparecem (ex.: Driver_8 em Las Vegas) NÃO são bug:** acontecem quando o jogador real configurou `showOnlineNames=OFF` E o lobby do F1 25 também recebeu o nome dele como `"Player"` (genérico). É uma limitação do jogo — sem nome real em nenhum pacote, o plugin não tem como inferi-lo. O slot é preservado nos resultados (com o tag `Driver_X`) porque ele tem laps válidas

### Added
- Test 14 em `Test-Finalizer.ps1`: simula UNAcapeleto-style scenario (lobby tem o nome, ci no overflow, 0 laps, slot virou AI) e verifica que o piloto é preservado nos resultados

## [1.1.29] - 2026-05-04

### Fixed
- **Qualifying online — pilotos fantasma `Driver_X` em grids parciais:** quando o lobby não tinha 20 pilotos, os slots de overflow (carIdx ≥ `participantsPeakNumActive`) que o jogo preenche com AI fillers ainda apareciam nos resultados como `Driver_18`, `Driver_19`, etc. Validado contra OTKs reais: Monaco (peak=18, 2 phantoms), Miami (peak=19, 1 phantom), Baku (peak=16, 4 phantoms). Corridas e qualifying com grid completo (peak=20) não são afetados
- **Causa raiz:** o flag `AiControlled` do `ParticipantData` pode ficar stale (incorretamente `false`) em pacotes posteriores quando o jogo reduz `NumActiveCars`, fazendo `IsPhantomEntry` não pegar essas entradas. Além disso, `ResolveNamesFromLobby` e o loop pós-FC em `IngestFinalClassification` registravam placeholders para todos os 22 slots de `TeamByCarIdx`, criando os Drivers fantasmas

### Added
- Defesa em profundidade: 5 filtros independentes em `SessionStore` (`ResolveNamesFromLobby`, `IngestFinalClassification` main loop, post-FC registration loop) e `LeagueFinalizer` (`IsPhantomEntry`, `ShouldSkipFcAiGridFillerRow`)
- Todos os filtros gated em `NetworkGame == 1` (online only) e protegidos por `HumanCarIdxs[carIdx]` — nunca filtram um carIdx que foi confirmado como humano em algum momento da sessão
- Test 13 em `Test-Finalizer.ps1`: cobre 4 cenários (Monaco-style peak=18, Miami-style peak=19, Baku-style peak=16, full grid peak=20)

## [1.1.28] - 2026-04-25

### Fixed
- **Custom MyTeam online — colisão de `raceNumber` (issue #1):** lobbies de Equipes AV (My Team customizado) podiam atribuir o mesmo `raceNumber` a dois jogadores diferentes — bug conhecido EA confirmado em F1 24/25. Quando isso acontecia, a chave `raceNumber_teamId` era roubada de um jogador para o slot do outro: um aparecia como `Driver_X`/`Car_X` (placeholder) enquanto o outro acabava com a telemetria misturada (caso do upload Jeddah/Equipes AV onde `Bruno Kauan` foi exportado como `Car_17`/`Driver_17` e seu nome foi herdado por outro carIdx). Solução: prioridade no `m_networkId` (offset 2 do `ParticipantData`) como chave única de jogador online
- **AI guard:** slots controlados por IA não podem mais herdar o nome de um `carIdx` confirmado humano via colisão de `(raceNumber, teamId)` — protege contra fillers de grid roubarem gamertags reais

### Added
- `ParticipantEntry.NetworkId` parseado do offset 2 (`m_networkId`) — campo antes ignorado pelo parser
- Mapa `_bestKnownTagsByNet` (`net{networkId}_{teamId}` → nome real) populado para humanos confirmados (immune a colisões de `raceNumber`)
- `_rnKeyAmbiguous`: HashSet de chaves `raceNumber_teamId` poluídas (>1 humanos com mesma `(rn, teamId)` num packet ou conflito detectado em escrita). Lookups via rn-key pulam essas chaves
- `SessionStore.LookupBestKnownTagForEntry(entry)` — API pública usada por `LeagueFinalizer.RetroResolveNames` e pelo loop de fallback FC; aplica prioridade `net-key → rn-key (se não-ambígua) → teamId-only`
- Diagnósticos exportados: `lobbyInfo.bestKnownTagsByNet`, `lobbyInfo.rnKeyAmbiguous`
- Notas em `Notes`: `rn-key ambiguous: ...` (detecção pré-loop) e `rn-key conflict on write: ...` (detecção on-write)
- Testes 16/17/18 em `Test-SessionStore.ps1` para colisão Custom MyTeam, resolução por `networkId` quando `showOnlineNames=0`, e AI guard

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
