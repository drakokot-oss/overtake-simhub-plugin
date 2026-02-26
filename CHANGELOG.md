# Changelog

All notable changes to the Overtake SimHub Plugin are documented here.

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
