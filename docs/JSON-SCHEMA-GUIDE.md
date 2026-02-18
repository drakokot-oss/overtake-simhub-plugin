# Overtake Telemetry (SimHub Plugin) — JSON Schema Guide

Schema version: **league-1.0**
Plugin: **Overtake Telemetry for SimHub**

---

## Arquivo gerado

**Nome do arquivo:** `{CircuitName}_{Data}_{Horario}_{CodigoUnico}.json`

Exemplo: `Suzuka_20260218_153045_A2B3C4.json`

| Parte | Formato | Exemplo |
|-------|---------|---------|
| CircuitName | Nome do circuito sem espaços | `Suzuka`, `Monza`, `LasVegas` |
| Data | `yyyyMMdd` | `20260218` |
| Horario | `HHmmss` | `153045` |
| CodigoUnico | 6 caracteres hex do session UID | `A2B3C4` |

---

## Estrutura geral

```json
{
  "schemaVersion": "league-1.0",
  "game": "F1_25",
  "capture": { ... },
  "participants": [ ... ],
  "sessions": [ ... ],
  "_debug": { ... }
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `schemaVersion` | `string` | Sempre `"league-1.0"`. Use para validar compatibilidade. |
| `game` | `string` | Sempre `"F1_25"`. |
| `capture` | `object` | Metadados da captura (quando começou/terminou). |
| `participants` | `string[]` | Lista de todas as tags (nomes) dos pilotos vistos na captura. |
| `sessions` | `array` | **Array principal.** Cada item é uma sessão (Qualifying, Race, etc.). |
| `_debug` | `object` | Dados de debug. **Ignorar no frontend.** |

---

## `capture`

```json
{
  "sessionUID": "18266826124105061939",
  "startedAtMs": 1770603555824,
  "endedAtMs": 1770604127601,
  "source": {},
  "sessionTypesInCapture": ["OneShotQualifying", "Race"]
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `sessionUID` | `string` | ID único da última sessão capturada. |
| `startedAtMs` | `number` | Timestamp (ms) de quando o plugin começou a capturar. |
| `endedAtMs` | `number` | Timestamp (ms) de quando o JSON foi exportado. |
| `sessionTypesInCapture` | `string[]` | Lista dos tipos de sessão presentes (ex: `["OneShotQualifying", "Race"]`). |

---

## `sessions[]` — O array principal

Cada sessão é um objeto completo. Um arquivo pode conter **Qualifying + Race** (2 itens) ou só **Race** (1 item), etc.

```json
{
  "sessionUID": "18266826124105061939",
  "sessionType": { "id": 10, "name": "Race" },
  "track": { "id": 26, "name": "Zandvoort" },
  "weather": { "id": 1, "name": "LightCloud" },
  "trackTempC": 24,
  "airTempC": 18,
  "weatherTimeline": [ ... ],
  "weatherForecast": [ ... ],
  "lastPacketMs": 1770604126263,
  "sessionEndedAtMs": 1770604102591,
  "safetyCar": { ... },
  "networkGame": false,
  "awards": { ... },
  "results": [ ... ],
  "drivers": { ... },
  "events": [ ... ]
}
```

### Campos da sessão

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `sessionUID` | `string` | ID único da sessão no jogo. |
| `sessionType.id` | `number` | ID numérico do tipo de sessão. |
| `sessionType.name` | `string` | Nome legível: `"Race"`, `"OneShotQualifying"`, `"ShortQualifying"`, etc. |
| `track.id` | `number` | ID do circuito (ex: 26 = Zandvoort). |
| `track.name` | `string` | Nome do circuito: `"Zandvoort"`, `"Montreal"`, `"Monza"`, etc. |
| `weather.id` | `number` | **Último** estado do clima: 0=Clear, 1=LightCloud, 2=Overcast, 3=LightRain, 4=HeavyRain, 5=Storm. |
| `weather.name` | `string` | Nome legível do clima final. |
| `trackTempC` | `number\|null` | Temperatura da pista em °C (último valor). |
| `airTempC` | `number\|null` | Temperatura do ar em °C (último valor). |
| `weatherTimeline` | `array` | Timeline de mudanças de clima ao longo da sessão. |
| `weatherForecast` | `array` | Previsão do tempo do jogo (último snapshot). |
| `lastPacketMs` | `number` | Timestamp do último pacote recebido nesta sessão. |
| `sessionEndedAtMs` | `number\|null` | Timestamp de quando a sessão terminou. `null` se não terminou. |
| `safetyCar` | `object` | Dados do Safety Car e Red Flags. |
| `networkGame` | `boolean` | `true` se lobby online, `false` se offline. |
| `awards` | `object` | Prêmios calculados: volta mais rápida, mais consistente, mais posições ganhas. |
| `results` | `array` | Classificação final — ordenada por posição. |
| `drivers` | `object` | Dados detalhados por piloto — voltas, stints, pit stops, desgaste, penalidades. |
| `events` | `array` | Lista de eventos (colisões, penalidades, ultrapassagens, etc.). |

---

## `sessions[].weatherTimeline[]`

Array que registra cada mudança de clima ao longo da sessão. Se tem 1 entrada = clima constante. Se tem 2+ = clima mudou.

```json
[
  { "tsMs": 1770646010000, "weather": { "id": 3, "name": "LightRain" }, "trackTempC": 18, "airTempC": 15 },
  { "tsMs": 1770646120000, "weather": { "id": 2, "name": "Overcast" }, "trackTempC": 20, "airTempC": 16 }
]
```

### IDs de clima

| ID | Nome | Descrição |
|----|------|-----------|
| 0 | Clear | Céu limpo, pista seca |
| 1 | LightCloud | Nublado leve, pista seca |
| 2 | Overcast | Nublado pesado, pista seca |
| 3 | LightRain | Chuva leve, pista molhada |
| 4 | HeavyRain | Chuva forte, pista muito molhada |
| 5 | Storm | Tempestade |

---

## `sessions[].weatherForecast[]`

```json
{
  "timeOffsetMin": 5,
  "weather": { "id": 0, "name": "Clear" },
  "trackTempC": 24,
  "airTempC": 18,
  "rainPercentage": 10
}
```

---

## `sessions[].results[]` — Classificação final

Já vem **ordenado por posição** (P1 primeiro).

```json
{
  "position": 1,
  "tag": "VERSTAPPEN",
  "teamId": 2,
  "teamName": "Red Bull Racing",
  "grid": 1,
  "numLaps": 50,
  "bestLapTimeMs": 71195,
  "bestLapTime": "1:11.195",
  "totalTimeMs": 359728,
  "totalTime": "5:59.728",
  "penaltiesTimeSec": 0,
  "pitStops": 0,
  "status": "Finished",
  "numPenalties": 0
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `position` | `number` | Posição final (1, 2, 3...). |
| `tag` | `string` | Nome/tag do piloto. Use para cruzar com `drivers`. |
| `teamId` | `number` | ID da equipe. |
| `teamName` | `string` | Nome da equipe. `"MyTeam"` para carros criados no modo My Team. |
| `grid` | `number\|null` | Posição no grid de largada. `null` se indisponível. |
| `numLaps` | `number` | Número de voltas completadas. |
| `bestLapTimeMs` | `number\|null` | Melhor volta em milissegundos. |
| `bestLapTime` | `string` | Melhor volta formatada. |
| `totalTimeMs` | `number\|null` | Tempo total da corrida em ms (sem penalidades). |
| `totalTime` | `string` | Tempo total formatado. |
| `penaltiesTimeSec` | `number` | Total de segundos de penalidade aplicados pelo jogo. |
| `pitStops` | `number` | Número de pit stops. |
| `status` | `string` | Status final: `"Finished"`, `"DidNotFinish"`, `"Disqualified"`, `"Retired"`, `"NotClassified"`. |
| `numPenalties` | `number` | Número de penalidades recebidas. |

### Como calcular o gap entre pilotos

```
gap_ms = piloto.totalTimeMs - vencedor.totalTimeMs
```

### Reclassificação com penalidades pós-corrida

Para o sistema de incidentes da liga, calcule:

```javascript
adjustedTimeMs = totalTimeMs + (penaltiesTimeSec * 1000) + (stewardPenaltySec * 1000)
```

Ordene apenas pilotos com mesmo `numLaps`. Pilotos com mais voltas ficam sempre acima.

---

## `sessions[].drivers{}` — Dados detalhados por piloto

Objeto onde a **chave é a tag do piloto**. Cada valor contém voltas, stints, desgaste de pneu e timelines.

```json
{
  "VERSTAPPEN": {
    "position": 0,
    "teamId": 2,
    "teamName": "Red Bull Racing",
    "myTeam": false,
    "raceNumber": 1,
    "aiControlled": true,
    "isPlayer": false,
    "platform": "Steam",
    "showOnlineNames": true,
    "yourTelemetry": "public",
    "nationality": 75,
    "laps": [ ... ],
    "tyreStints": [ ... ],
    "tyreWearPerLap": [ ... ],
    "damagePerLap": [ ... ],
    "wingRepairs": [ ... ],
    "best": { ... },
    "pitStopsTimeline": [ ... ],
    "penaltiesTimeline": [ ... ],
    "collisionsTimeline": [ ... ],
    "totalWarnings": 0,
    "cornerCuttingWarnings": 0
  }
}
```

### Campo `isPlayer` (novo no plugin SimHub)

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `isPlayer` | `boolean` | `true` se este é o carro controlado pelo jogador que está rodando o plugin. `false` para IA e outros jogadores. |

Útil para o frontend destacar o piloto do usuário na tabela de resultados.

### `drivers[tag].laps[]`

```json
{
  "lapNumber": 1,
  "lapTimeMs": 74634,
  "lapTime": "1:14.634",
  "sector1Ms": 28032,
  "sector2Ms": 24915,
  "sector3Ms": 21686,
  "valid": true,
  "flags": ["Valid"],
  "tsMs": 1770604001234
}
```

### `drivers[tag].tyreStints[]`

```json
{
  "endLap": 255,
  "tyreActualId": 17,
  "tyreActual": "C4",
  "tyreVisualId": 16,
  "tyreVisual": "Soft"
}
```

**Mapeamento de pneus visuais:**

| tyreVisualId | tyreVisual | Cor |
|---|---|---|
| 16 | Soft | Vermelho |
| 17 | Medium | Amarelo |
| 18 | Hard | Branco |
| 7 | Intermediate | Verde |
| 8 | Wet | Azul |

### `drivers[tag].tyreWearPerLap[]` — Desgaste (ACUMULADO)

```json
{ "lapNumber": 5, "rl": 3.2, "rr": 3.6, "fl": 1.2, "fr": 1.6, "avg": 2.4 }
```

**Valores são ACUMULADOS.** Para calcular degradação por volta, subtraia valores consecutivos. Quando `avg` diminui entre voltas, houve pit stop (pneus novos).

### `drivers[tag].damagePerLap[]`

```json
{ "lapNumber": 5, "wingFL": 0, "wingFR": 42, "wingRear": 0, "tyreDmgRL": 0, "tyreDmgRR": 0, "tyreDmgFL": 0, "tyreDmgFR": 0 }
```

### `drivers[tag].wingRepairs[]`

```json
{ "lap": 12, "wing": "frontRightWing", "damageBefore": 42, "damageAfter": 0, "repaired": 42 }
```

Reparos de asa detectados automaticamente quando o dano cai 10%+ entre voltas (tipicamente durante pit stop).

### `drivers[tag].best`

```json
{
  "bestLapTimeLapNum": 4,
  "bestLapTimeMs": 101224,
  "bestSector1LapNum": 5,
  "bestSector1Ms": 30115,
  "bestSector2LapNum": 4,
  "bestSector2Ms": 33675,
  "bestSector3LapNum": 3,
  "bestSector3Ms": 38138
}
```

### `drivers[tag].penaltiesTimeline[]`

Contém avisos e penalidades. Colisões brutas ficam em `collisionsTimeline` (separado).

```json
{
  "tsMs": 1770604030000,
  "category": "penalty",
  "penaltyType": 4,
  "penaltyTypeName": "TimePenalty",
  "infringementType": 7,
  "infringementTypeName": "CornerCuttingGainedTime",
  "otherDriver": "HAMILTON",
  "timeSec": 5,
  "lapNum": 12
}
```

| category | Significado |
|----------|-------------|
| `"warning"` | Aviso (sem punição direta) |
| `"penalty"` | Punição real (DT, Stop-Go, tempo) |
| `"disqualification"` | Desqualificação (DSQ) |
| `"retired"` | Abandono registrado |
| `"other"` | Outros (grid penalty, etc.) |

### `drivers[tag].collisionsTimeline[]`

```json
{ "tsMs": 1770604030000, "type": "collision" }
```

### `drivers[tag].pitStopsTimeline[]`

```json
{ "numPitStops": 1, "tsMs": 1770604050000, "lapNum": 15 }
```

---

## `sessions[].safetyCar`

```json
{
  "status": { "id": 0, "name": "NoSafetyCar" },
  "fullDeploys": 1,
  "vscDeploys": 1,
  "redFlagPeriods": 0,
  "lapsUnderSC": [5, 6, 7],
  "lapsUnderVSC": [12, 13]
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `status.name` | `string` | Último status: `"NoSafetyCar"`, `"FullSafetyCar"`, `"VirtualSafetyCar"`. |
| `fullDeploys` | `number` | Vezes que o SC real foi acionado. |
| `vscDeploys` | `number` | Vezes que o VSC foi acionado. |
| `redFlagPeriods` | `number` | Vezes que a bandeira vermelha foi acionada. |
| `lapsUnderSC` | `number[]` | Números das voltas sob Safety Car real. Array vazio se não houve SC. |
| `lapsUnderVSC` | `number[]` | Números das voltas sob Virtual Safety Car. Array vazio se não houve VSC. |

**Como o cálculo é feito:** O plugin usa interpolação linear entre os timestamps de `LGOT` (largada) e `CHQF` (bandeira quadriculada) para mapear os períodos de SC/VSC (eventos `SCAR`) para números de volta. Se não houver LGOT/CHQF (ex: sessão não finalizada), os arrays ficam vazios.

---

## `sessions[].awards`

```json
{
  "fastestLap": {
    "tag": "VERSTAPPEN",
    "timeMs": 71195,
    "time": "1:11.195"
  },
  "mostConsistent": {
    "tag": "NORRIS",
    "stdDevMs": 2836,
    "stdDev": "2.836",
    "cleanLaps": 13
  },
  "mostPositionsGained": {
    "tag": "ALONSO",
    "grid": 18,
    "finish": 6,
    "gained": 12
  }
}
```

Cada prêmio pode ser `null` se não houver dados suficientes.

**fastestLap:** Usa o evento FTLP do jogo. Se indisponível, usa o menor bestLapTimeMs dos results.

**mostConsistent:** Desvio padrão dos tempos de volta. Exclui volta 1, voltas de pit, e outliers. Mínimo 5 voltas limpas. Apenas pilotos no top 50% por posição final são elegíveis.

**mostPositionsGained:** grid - finish. Apenas pilotos com status "Finished". Desempate: melhor posição final.

---

## `sessions[].events[]`

```json
{
  "tsMs": 1770604012345,
  "code": "PENA",
  "name": "PenaltyIssued",
  "data": {
    "penaltyType": 4,
    "penaltyTypeName": "TimePenalty",
    "infringementType": 7,
    "infringementTypeName": "CornerCuttingGainedTime",
    "vehicleIdx": 5,
    "vehicleTag": "STROLL",
    "timeSec": 5,
    "lapNum": 3
  }
}
```

### Códigos de evento

| Code | Name | Descrição |
|------|------|-----------|
| `SSTA` | SessionStarted | Sessão iniciou |
| `SEND` | SessionEnded | Sessão terminou |
| `LGOT` | LightsOut | Largada |
| `SCAR` | SafetyCarDeployed | Safety Car acionado |
| `VSCN` | VSCDeployed | Virtual Safety Car acionado |
| `VSCE` | VSCEnded | Virtual Safety Car encerrado |
| `OVTK` | Overtake | Ultrapassagem |
| `COLL` | Collision | Colisão |
| `PENA` | PenaltyIssued | Penalidade aplicada |
| `RTMT` | Retirement | Piloto abandonou |
| `FTLP` | FastestLap | Volta mais rápida |
| `CHQF` | ChequeredFlag | Bandeira quadriculada |
| `RCWN` | RaceWinner | Vencedor anunciado |
| `RDFL` | RedFlag | Bandeira vermelha |
| `DTSV` | DriveThroughServed | Drive-through cumprido |
| `SGSV` | StopGoServed | Stop-and-go cumprido |

---

## Tipos de sessão

| name | Descrição |
|------|-----------|
| `Practice1` / `Practice2` / `Practice3` | Treinos livres |
| `ShortPractice` | Treino curto |
| `Qualifying1` / `Qualifying2` / `Qualifying3` | Q1, Q2, Q3 |
| `ShortQualifying` | Qualy curta |
| `OneShotQualifying` | Qualy one-shot |
| `Race` | Corrida |
| `Sprint` | Sprint |
| `SprintShootout` | Sprint Shootout |

---

## Status de resultado

| status | Descrição | Exibir como |
|--------|-----------|-------------|
| `"Finished"` | Completou a corrida | Mostrar tempo/gap |
| `"DidNotFinish"` | Não terminou (DNF) | "DNF" |
| `"Disqualified"` | Desqualificado (DSQ) | "DSQ" |
| `"NotClassified"` | Não classificado | "NC" |
| `"Retired"` | Abandonou | "RET" |

---

## Equipes (teamId -> teamName)

| teamId | teamName |
|--------|----------|
| 0 | Mercedes-AMG Petronas |
| 1 | Scuderia Ferrari HP |
| 2 | Red Bull Racing |
| 3 | Williams Racing |
| 4 | Aston Martin Aramco |
| 5 | Alpine F1 Team |
| 6 | Visa Cash App Racing Bulls |
| 7 | MoneyGram Haas F1 Team |
| 8 | McLaren Formula 1 Team |
| 9 | Stake F1 Team Kick Sauber |

---

## Circuitos (track.id -> track.name)

| id | name | | id | name |
|----|------|-|----|------|
| 0 | Melbourne | | 16 | Brazil |
| 2 | Shanghai | | 17 | Austria |
| 3 | Sakhir | | 19 | Mexico |
| 4 | Catalunya | | 20 | Baku |
| 5 | Monaco | | 26 | Zandvoort |
| 6 | Montreal | | 27 | Imola |
| 7 | Silverstone | | 29 | Jeddah |
| 9 | Hungaroring | | 30 | Miami |
| 10 | Spa | | 31 | LasVegas |
| 11 | Monza | | 32 | Losail |
| 12 | Singapore | | 33 | Lusail |
| 13 | Suzuka | | 39 | Silverstone Reverse |
| 14 | AbuDhabi | | 40 | Austria Reverse |
| 15 | Texas | | 41 | Zandvoort Reverse |

---

## Diferenças em relação ao app standalone (Overtake Telemetry)

O plugin SimHub gera o **mesmo schema `league-1.0`** com as seguintes diferenças:

| Aspecto | App Standalone | Plugin SimHub |
|---------|---------------|---------------|
| Nome do arquivo | `league_{uid}_{ts}.json` | `{Circuit}_{Date}_{Time}_{Code}.json` |
| `drivers[tag].isPlayer` | Não existia | **Novo.** `true` se é o carro do jogador |
| `results[].numWarnings` | Presente (nullable) | Não incluído |
| `results[].numPenaltyPoints` | Presente (nullable) | Não incluído |
| `results[].numDriveThroughPens` | Presente (nullable) | Não incluído |
| `results[].numStopGoPens` | Presente (nullable) | Não incluído |
| `safetyCar.lapsUnderSC` | Presente | **Presente** (calculado via interpolação) |
| `safetyCar.lapsUnderVSC` | Presente | **Presente** (calculado via interpolação) |

**O frontend deve tratar os campos de `results[]` acima como opcionais** (verificar se existem antes de usar). Os dados essenciais (results, drivers, events, awards, safetyCar) são idênticos entre ambas as versões.

---

## Notas importantes para o frontend

1. **Sempre filtre por `sessionType.name`** para encontrar a corrida/qualy desejada.
2. **`results[]` já vem ordenado** pela classificação original do jogo.
3. **Cruze `results[].tag` com `drivers[tag]`** para obter as voltas detalhadas.
4. **`_debug` é interno** — não exibir no site.
5. **Tempos em ms** — divida por 1000 e formate com 3 casas decimais.
6. **`isPlayer`** — use para destacar o piloto do usuário nos resultados.
7. **Carros fantasma são filtrados** — o JSON só contém pilotos reais.
8. **Sessões duplicadas são deduplificadas** — apenas a última de cada tipo é mantida.
9. **Para pilotos humanos online**, a tag pode ser o gamertag. Para IA, é o sobrenome.
10. **`showOnlineNames: false`** indica que o jogador não exibiu o nome real. Para ligas, exija que todos ativem.
11. **`yourTelemetry: "restricted"`** significa que dados como `tyreWearPerLap` vêm zerados.
12. **`myTeam: true`** significa `teamName = "MyTeam"`. O frontend deve permitir renomear.
