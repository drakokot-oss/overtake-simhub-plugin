# Overtake Telemetry (SimHub Plugin) — JSON Schema Guide

Schema version: **league-1.1**
Plugin: **Overtake Telemetry for SimHub** (≥ v1.1.34 emite `league-1.1`; v1.1.35 ajustou agregados de ERS — ver [Histórico do schema](#histórico-do-schema))

> Este documento descreve **toda** a estrutura do `.json` (interno do `.otk`) gerado pelo plugin SimHub para o frontend (`racehub.overtakef1.com`). Use-o como contrato de integração.

---

## Arquivo gerado

**Nome do arquivo:** `{CircuitName}_{Data}_{Horario}_{CodigoUnico}.otk`

Exemplo: `Spa_20260512_234130_F0EB39.otk`

| Parte | Formato | Exemplo |
|-------|---------|---------|
| CircuitName | Nome do circuito sem espaços | `Spa`, `Monza`, `LasVegas` |
| Data | `yyyyMMdd` | `20260512` |
| Horario | `HHmmss` | `234130` |
| CodigoUnico | 6 caracteres hex do session UID | `F0EB39` |

> O `.otk` é um container criptografado (AES-CBC + HMAC-SHA256). Após decifrar, o conteúdo é o JSON descrito abaixo.

---

## Estrutura geral

```json
{
  "schemaVersion": "league-1.1",
  "game": "F1_25",
  "capture": { ... },
  "participants": [ ... ],
  "sessions": [ ... ],
  "_debug": { ... }
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `schemaVersion` | `string` | Sempre `"league-1.1"` (v1.1.34+). Use para validar compatibilidade. |
| `game` | `string` | Identificador do jogo/conteúdo que gerou a captura. **v1.1.36+**: derivado do `PacketHeader.PacketFormat` (`2025 → "F1_25"`, `2026 → "F1_26"`). **v1.1.39+**: agora é **content-aware** — se aparecerem equipes do F1 26 (`teamId` 220–230) ou o circuito de Madri (`track 42`), o valor é `"F1_26"` mesmo quando o `packetFormat` ainda é `2025` (o "2026 Season Pack" roda dentro do F1 25 e por padrão emite o formato de pacote 2025). O rótulo derivado só do formato fica em `_debug.game.formatLabel`. Use `game` para branding/mappings condicionais (Cadillac, Audi etc. só em `"F1_26"`). Capturas anteriores à v1.1.36 sempre serão `"F1_25"`. |
| `capture` | `object` | Metadados da captura (quando começou/terminou). |
| `participants` | `string[]` | Tags (nomes) de todos os pilotos vistos na captura. **Já filtrado de fantasmas** (Camadas 1–6). |
| `sessions` | `array` | **Array principal.** Cada item é uma sessão (Qualifying, Race, etc.). |
| `_debug` | `object` | Dados internos (notes, diagnostics). **Ignorar no frontend.** |

---

## `capture`

```json
{
  "sessionUID": "10595608129078094649",
  "startedAtMs": 1778635441331,
  "endedAtMs":   1778640089909,
  "source": {},
  "sessionTypesInCapture": ["ShortQualifying", "Race"]
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `sessionUID` | `string` | ID único da última sessão capturada. |
| `startedAtMs` | `number` | Timestamp (ms) de quando o plugin começou a capturar. |
| `endedAtMs` | `number` | Timestamp (ms) de quando o JSON foi exportado. |
| `source` | `object` | Reservado. Atualmente vazio (`{}`). |
| `sessionTypesInCapture` | `string[]` | Lista dos tipos de sessão presentes (ex: `["ShortQualifying", "Race"]`). |

---

## `participants[]` (top-level)

Lista plana, **deduplicada e em ordem alfabética**, das tags de todos os pilotos reais que apareceram na captura. É a "verdade global" — útil para validar que o número bate com o esperado da liga.

```json
[
  "Drako%",
  "JACK TAXISTA",
  "KRT_WaLTeR",
  "Vortex_Dudu Costa",
  ...
]
```

> Se a quantidade aqui for **maior** que o número de pilotos humanos esperados, é possível que tenha vazado um carro fantasma e o plugin precisa de hotfix. Reportar com o `.otk`.

---

## `sessions[]` — O array principal

Cada sessão é um objeto completo. Um arquivo pode conter **Qualifying + Race** (2 itens) ou só **Race** (1 item), etc.

```json
{
  "sessionUID": "10595608129078094649",
  "sessionType": { "id": 15, "name": "Race" },
  "track": { "id": 10, "name": "Spa" },
  "weather": { "id": 1, "name": "LightCloud" },
  "trackTempC": 25,
  "airTempC": 18,
  "weatherTimeline": [ ... ],
  "weatherForecast": [ ... ],
  "forecastAccuracy": "Perfect",
  "isSpectating": true,
  "lastPacketMs": 1778640089721,
  "sessionEndedAtMs": 1778640089721,
  "safetyCar": { ... },
  "networkGame": true,
  "lobbySettings": { ... },
  "participantsPeakNumActive": 19,
  "awards": { ... },
  "results": [ ... ],
  "drivers": { ... },
  "events": [ ... ],
  "exportDiagnostics": { ... }
}
```

### Campos da sessão

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `sessionUID` | `string` | ID único da sessão no jogo. |
| `sessionType.id` | `number` | ID numérico do tipo de sessão. |
| `sessionType.name` | `string` | Nome legível: `"Race"`, `"OneShotQualifying"`, `"ShortQualifying"`, etc. |
| `track.id` | `number` | ID do circuito (ex: 10 = Spa). |
| `track.name` | `string` | Nome do circuito: `"Spa"`, `"Monza"`, etc. |
| `weather.id` | `number` | **Último** estado do clima: 0=Clear, 1=LightCloud, 2=Overcast, 3=LightRain, 4=HeavyRain, 5=Storm. |
| `weather.name` | `string` | Nome legível do clima final. |
| `trackTempC` | `number\|null` | Temperatura da pista em °C (último valor). |
| `airTempC` | `number\|null` | Temperatura do ar em °C (último valor). |
| `weatherTimeline` | `array` | Timeline de mudanças de clima ao longo da sessão. |
| `weatherForecast` | `array` | Previsão do tempo do jogo (último snapshot). |
| `forecastAccuracy` | `string` | Acurácia da previsão: `"Perfect"`, `"Approximate"`. |
| `isSpectating` | `boolean` | `true` se o usuário do plugin estava em modo espectador. |
| `lastPacketMs` | `number` | Timestamp do último pacote recebido nesta sessão. |
| `sessionEndedAtMs` | `number\|null` | Timestamp de quando a sessão terminou. `null` se não terminou. |
| `safetyCar` | `object` | Dados do Safety Car e Red Flags. |
| `networkGame` | `boolean` | `true` se lobby online, `false` se offline. |
| `lobbySettings` | `object\|null` | Configurações do lobby (regras, assists, dano). Pode ser `null` em sessão singleplayer. |
| `participantsPeakNumActive` | `number` | Pico de carros ativos visto pelo plugin durante a sessão. Usado para detectar overflow. |
| `awards` | `object` | Prêmios calculados: volta mais rápida, mais consistente, mais posições ganhas. |
| `results` | `array` | Classificação final — ordenada por posição. |
| `drivers` | `object` | Dados detalhados por piloto — voltas, stints, telemetria, penalidades. |
| `events` | `array` | Lista de eventos (colisões, penalidades, ultrapassagens, etc.). |
| `exportDiagnostics` | `object` | Métricas internas de captura (números de pacotes processados, etc.). **Ignorar no frontend.** |

---

## `sessions[].weatherTimeline[]`

Array que registra cada mudança de clima ao longo da sessão. Se tem 1 entrada = clima constante. Se tem 2+ = clima mudou.

```json
[
  { "tsMs": 1770646010000, "weather": { "id": 3, "name": "LightRain" }, "trackTempC": 18, "airTempC": 15 },
  { "tsMs": 1770646120000, "weather": { "id": 2, "name": "Overcast" },  "trackTempC": 20, "airTempC": 16 }
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

Snapshot da previsão do tempo do jogo no momento do export.

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

## `sessions[].lobbySettings` (NOVO em league-1.1)

Configurações completas do lobby online. Útil para confirmar que a corrida foi disputada com regras de liga válidas.

```json
{
  "safetyCarAndFlags": {
    "safetyCar": "Reduced",
    "redFlags":  "Off"
  },
  "rulesAndSimulation": {
    "ruleSet":                       "Race",
    "collisions":                    "On",
    "collisionsOffForFirstLapOnly":  false,
    "cornerCuttingStringency":       "Strict",
    "parcFermeRules":                true,
    "formationLap":                  false,
    "equalCarPerformance":           true
  },
  "damageAndRealism": {
    "carDamage":      "Standard",
    "carDamageRate":  "Reduced",
    "surfaceType":    "Realistic",
    "lowFuelMode":    "Hard",
    "tyreTemperature":"Surface & Carcass",
    "pitLaneTyreSim": true
  },
  "assists": {
    "steeringAssist":         false,
    "brakingAssist":          "Off",
    "gearboxAssist":          "Manual",
    "pitAssist":              false,
    "pitReleaseAssist":       false,
    "ersAssist":              false,
    "drsAssist":              false,
    "dynamicRacingLine":      "Off",
    "dynamicRacingLineType":  "2D",
    "raceStarts":             "Manual",
    "recoveryMode":           "None",
    "flashbackLimit":         "Unlimited"
  }
}
```

> Os enums (`"Off"`, `"Standard"`, `"Reduced"`, etc.) seguem os labels do jogo. Para validação automática (ex: "esta corrida foi com `equalCarPerformance: true`?"), basta comparar strings.

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
| `lapsUnderSC` | `number[]` | Voltas sob Safety Car real. Vazio se não houve SC. |
| `lapsUnderVSC` | `number[]` | Voltas sob Virtual Safety Car. Vazio se não houve VSC. |

> O cálculo de `lapsUnderSC`/`lapsUnderVSC` usa interpolação linear entre os timestamps de `LGOT` (largada) e `CHQF` (bandeira quadriculada) para mapear os períodos de SC/VSC (eventos `SCAR`) para números de volta. Se não houver LGOT/CHQF (ex: sessão não finalizada), os arrays ficam vazios.

---

## `sessions[].awards`

```json
{
  "fastestLap": {
    "tag": "UNA_HiSeR-I1IAN",
    "timeMs": 113976,
    "time": "1:53.976"
  },
  "mostConsistent": {
    "tag": "Leriam_",
    "stdDevMs": 2823,
    "stdDev": "2.823",
    "cleanLaps": 21
  },
  "mostPositionsGained": {
    "tag": "Vortex_Boina",
    "grid": 16,
    "finish": 3,
    "gained": 13
  }
}
```

Cada prêmio pode ser `null` se não houver dados suficientes.

- **`fastestLap`** — Usa o evento FTLP do jogo. Se indisponível, usa o menor `bestLapTimeMs` dos `results`.
- **`mostConsistent`** — Desvio padrão dos tempos de volta. Exclui volta 1, voltas de pit e outliers. Mínimo 5 voltas limpas. Apenas pilotos no top 50% por posição final são elegíveis.
- **`mostPositionsGained`** — `grid - finish`. Apenas pilotos com status `"Finished"`. Desempate: melhor posição final.

---

## `sessions[].results[]` — Classificação final

Já vem **ordenado por posição** (P1 primeiro).

```json
{
  "position": 1,
  "tag": "Vortex_Dudu Costa",
  "carIdx": 4,
  "raceNumber": 57,
  "teamId": 5,
  "teamName": "Alpine F1 Team",
  "grid": 5,
  "numLaps": 22,
  "bestLapTimeMs": 114441,
  "bestLapTime": "1:54.441",
  "totalTimeMs": 2598801,
  "totalTime": "43:18.801",
  "penaltiesTimeSec": 0,
  "pitStops": 1,
  "status": "Finished",
  "numPenalties": 0,
  "classifiedLapped": false,
  "classificationLeaderLaps": 22,
  "numWarnings": 2,
  "numDriveThroughPens": 0,
  "numStopGoPens": 0
}
```

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `position` | `number` | Posição final (1, 2, 3...). |
| `tag` | `string` | Nome/tag do piloto. **Use para cruzar com `drivers[tag]`**. |
| `carIdx` | `number` | Índice do carro no UDP do F1 25 (0–21). Útil para cruzar com `events[].data.vehicleIdx`. |
| `raceNumber` | `number` | Número do carro (definido no Customize ou no piloto IA). |
| `teamId` | `number` | ID da equipe. |
| `teamName` | `string` | Nome da equipe. `"MyTeam"` para carros criados no modo My Team. |
| `grid` | `number\|null` | Posição no grid de largada. `null` se indisponível. |
| `numLaps` | `number` | Voltas completadas. |
| `bestLapTimeMs` | `number\|null` | Melhor volta em ms. |
| `bestLapTime` | `string` | Melhor volta formatada. |
| `totalTimeMs` | `number\|null` | Tempo total em ms (sem penalidades aplicadas separadamente). |
| `totalTime` | `string` | Tempo total formatado. |
| `penaltiesTimeSec` | `number` | Total de segundos de penalidade aplicados pelo jogo. |
| `pitStops` | `number` | Número de pit stops. |
| `status` | `string` | `"Finished"`, `"DidNotFinish"`, `"Disqualified"`, `"Retired"`, `"NotClassified"`. |
| `numPenalties` | `number` | Número de penalidades recebidas. |
| `classifiedLapped` | `boolean` | `true` se o piloto está com 1+ voltas a menos que o líder (foi "lapped"). Apenas em `Race`. |
| `classificationLeaderLaps` | `number` | Voltas do líder na classificação final. Permite calcular `gap_laps = classificationLeaderLaps - numLaps`. |
| `numWarnings` | `number` | Total de avisos (corner cutting + outros). |
| `numDriveThroughPens` | `number` | Drive-through penalties recebidas. |
| `numStopGoPens` | `number` | Stop-Go penalties recebidas. |

### Como calcular o gap entre pilotos (Race)

```js
const gapMs = piloto.totalTimeMs - vencedor.totalTimeMs;
```

### Como detectar pilotos lapeados

```js
if (piloto.classifiedLapped) {
  const gapLaps = piloto.classificationLeaderLaps - piloto.numLaps;
  // exibir como "+1 LAP", "+2 LAPS"
}
```

### Reclassificação com penalidades pós-corrida (sistema de incidentes da liga)

```js
adjustedTimeMs = totalTimeMs + (penaltiesTimeSec * 1000) + (stewardPenaltySec * 1000);
```

Ordene **apenas pilotos com mesmo `numLaps`**. Pilotos com mais voltas ficam sempre acima.

---

## `sessions[].drivers{}` — Dados detalhados por piloto

Objeto onde a **chave é a tag do piloto** (mesma de `results[].tag`). Cada valor contém voltas, stints, desgaste, telemetria e timelines.

```json
{
  "Vortex_Dudu Costa": {
    "position": 0,
    "teamId": 5,
    "teamName": "Alpine F1 Team",
    "myTeam": false,
    "raceNumber": 57,
    "aiControlled": false,
    "isPlayer": false,
    "platform": "Steam",
    "showOnlineNames": true,
    "yourTelemetry": "public",
    "nationality": 9,
    "laps": [ ... ],
    "tyreStints": [ ... ],
    "tyreWearPerLap": [ ... ],
    "damagePerLap": [ ... ],
    "wingRepairs": [ ... ],
    "best": { ... },
    "pitStopsTimeline": [ ... ],
    "penaltiesTimeline": [ ... ],
    "collisionsTimeline": [ ... ],
    "totalWarnings": 2,
    "cornerCuttingWarnings": 2,
    "driverAssists": { ... },
    "fuelTelemetry": { ... },
    "ersTelemetry": { ... }
  }
}
```

### Campos do nível raiz do piloto

| Campo | Tipo | Descrição |
|-------|------|-----------|
| `position` | `number` | Posição reportada pelo jogo (geralmente 0 quando o piloto não cruzou linha). Use `results[].position` como fonte canônica. |
| `teamId` / `teamName` | `number` / `string` | Equipe. |
| `myTeam` | `boolean` | `true` se carro do modo My Team. |
| `raceNumber` | `number` | Número do carro. |
| `aiControlled` | `boolean` | `true` se IA, `false` se humano. |
| `isPlayer` | `boolean` | `true` se este é o carro do usuário do plugin. **Em modo espectador, será `false` para todos.** Use para destacar o "você" na UI. |
| `platform` | `string` | `"Steam"`, `"Origin"`, `"PlayStation"`, `"Xbox"`, `"Unknown"`. |
| `showOnlineNames` | `boolean` | `false` se o jogador desativou a exibição do nome online. |
| `yourTelemetry` | `string` | `"public"` ou `"restricted"`. Quando `"restricted"`, dados privados (desgaste de pneu, fuel, ERS) ficam zerados para os outros pilotos. |
| `nationality` | `number` | ID de nacionalidade do F1 25. |

### `drivers[tag].laps[]`

```json
{
  "lapNumber": 1,
  "lapTimeMs": 124029,
  "lapTime": "2:04.029",
  "sector1Ms": 37883,
  "sector2Ms": 55031,
  "sector3Ms": 31114,
  "valid": true,
  "flags": ["Valid"],
  "tsMs": 1778640089719
}
```

### `drivers[tag].tyreStints[]`

```json
{
  "endLap": 11,
  "tyreActualId": 7,
  "tyreActual": "Intermediate",
  "tyreVisualId": 7,
  "tyreVisual": "Intermediate"
}
```

> O último stint frequentemente vem com `endLap: 255` — significa "stint até o fim da sessão" (sentinel do UDP, não é uma volta real).

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

**Valores são ACUMULADOS** (% total gasto desde o início do stint). Para calcular degradação por volta, subtraia valores consecutivos. Quando `avg` **diminui** entre voltas, houve pit stop (pneus novos).

### `drivers[tag].damagePerLap[]`

```json
{ "lapNumber": 5, "wingFL": 0, "wingFR": 42, "wingRear": 0, "tyreDmgRL": 0, "tyreDmgRR": 0, "tyreDmgFL": 0, "tyreDmgFR": 0 }
```

Valores em % de dano (0–100).

### `drivers[tag].wingRepairs[]`

```json
{ "lap": 12, "wing": "frontRightWing", "damageBefore": 42, "damageAfter": 0, "repaired": 42 }
```

Reparos de asa detectados automaticamente quando o dano cai 10pp+ entre voltas (tipicamente durante pit stop).

### `drivers[tag].best`

```json
{
  "bestLapTimeLapNum": 15,
  "bestLapTimeMs":     114441,
  "bestSector1LapNum": 15,
  "bestSector1Ms":     32648,
  "bestSector2LapNum": 15,
  "bestSector2Ms":     51266,
  "bestSector3LapNum": 14,
  "bestSector3Ms":     30527
}
```

### `drivers[tag].pitStopsTimeline[]`

```json
{ "numPitStops": 1, "tsMs": 1778640050000, "lapNum": 11 }
```

### `drivers[tag].penaltiesTimeline[]`

Avisos e penalidades. Colisões brutas ficam em `collisionsTimeline` (separado).

```json
{
  "tsMs": 1778640030000,
  "category": "penalty",
  "penaltyType": 4,
  "penaltyTypeName": "TimePenalty",
  "infringementType": 7,
  "infringementTypeName": "CornerCuttingGainedTime",
  "otherDriver": "Drako%",
  "timeSec": 5,
  "lapNum": 12
}
```

| `category` | Significado |
|----------|-------------|
| `"warning"` | Aviso (sem punição direta) |
| `"penalty"` | Punição real (DT, Stop-Go, tempo) |
| `"disqualification"` | Desqualificação (DSQ) |
| `"retired"` | Abandono registrado |
| `"other"` | Outros (grid penalty, etc.) |

### `drivers[tag].collisionsTimeline[]`

```json
{ "tsMs": 1778640030000, "type": "collision" }
```

### `drivers[tag].driverAssists` (NOVO em league-1.1)

Configuração de assistência do piloto. Capturado a partir do `CarStatusData` (UDP packet 7). Em multiplayer, o jogo envia o valor mais "permissivo" no fim da corrida — o plugin guarda o valor **mais restritivo** visto durante a sessão.

```json
{
  "tractionControl": "Off",
  "antiLockBrakes":  false
}
```

| Campo | Tipo | Valores |
|---|---|---|
| `tractionControl` | `string` | `"Off"`, `"Medium"`, `"Full"` |
| `antiLockBrakes` | `boolean` | `true` = ABS ligado; `false` = ABS desligado |

> Para validação de liga: `tractionControl == "Off"` e `antiLockBrakes == false` é o setting mais comum em ligas competitivas.

### `drivers[tag].fuelTelemetry` (NOVO em league-1.1)

Snapshot de combustível. Pode ser `null` se nenhum pacote válido foi capturado (típico em sessões muito curtas ou se outro piloto está com `yourTelemetry: "restricted"`).

```json
{
  "fuelMix":                "Standard",
  "fuelCapacityKg":         110.0,
  "fuelInTankKgFirst":      50.681,
  "fuelInTankKgLast":        0.504,
  "fuelRemainingLapsFirst":  0.796,
  "fuelRemainingLapsLast":   0.986
}
```

| Campo | Tipo | Descrição |
|---|---|---|
| `fuelMix` | `string` | Último modo: `"Lean"`, `"Standard"`, `"Rich"`, `"Max"` |
| `fuelCapacityKg` | `number` | Capacidade do tanque (kg). |
| `fuelInTankKgFirst` / `Last` | `number` | Combustível no tanque na **primeira** e na **última** amostra (kg). |
| `fuelRemainingLapsFirst` / `Last` | `number` | Voltas restantes estimadas pelo jogo. |

### `drivers[tag].ersTelemetry` (NOVO em league-1.1, v1.1.34/35; **recalibrado em v1.1.42**)

Telemetria de bateria/ERS por piloto. `storePct*` = % da carga do reservatório (4 MJ = 100%, ambos os jogos). **A partir da v1.1.42**, deploy/harvest têm campos em **MJ absolutos** (recomendado) e os campos `%` passam a ser **% do teto de regulamento** daquele ano (deploy 9 MJ / MGU-K harvest 8,5 MJ no 2026; 4 MJ / 2 MJ no 2025), ficando limitados ~0–100%.

```json
{
  "storePctFirst":              100.0,
  "storePctLast":                25.0,
  "storePctMin":                  2.0,
  "storePctMax":                100.0,
  "storePctAvg":                 57.29,

  "deployedMjAvgPerLap":          7.02,
  "harvestedMgukMjAvgPerLap":     5.10,
  "harvestedMguhMjAvgPerLap":     0.0,

  "deployedPctPerLap":          [78.0, 80.1, 77.5, ...],
  "deployedPctAvgPerLap":        77.78,
  "harvestedMgukPctPerLap":     [60.1, 59.8, 60.0, ...],
  "harvestedMgukPctAvgPerLap":   58.82,
  "harvestedMguhPctPerLap":     [0.0, 0.0, ...],
  "harvestedMguhPctAvgPerLap":   0.0,

  "deployLimitMjPerLap":          9.0,
  "harvestMgukLimitMjPerLap":     8.5,
  "storeMaxMj":                   4.0,

  "deployModeLast":             "Medium",
  "samplesCount":                163492,
  "samplesPaused":                    0
}
```

| Campo | Tipo | Descrição |
|---|---|---|
| `storePctFirst` | `number` | Carga da bateria na primeira amostra (saída da garagem). Tipicamente `100`. |
| `storePctLast` | `number` | Carga da bateria na última amostra. |
| `storePctMin` / `storePctMax` | `number` | Mínimo / máximo observado. `0` = descarregou completamente em algum momento. |
| `storePctAvg` | `number` | **"economia média"** — % médio de carga da bateria (0–100). ~80% = guardador, ~30–50% = agressivo. |
| `deployedMjAvgPerLap` | `number` | **(v1.1.42, RECOMENDADO)** Energia média **deployada por volta em MJ**. Métrica universal (2025 e 2026). |
| `harvestedMgukMjAvgPerLap` | `number` | **(v1.1.42)** Regen média do MGU-K por volta, em MJ. |
| `harvestedMguhMjAvgPerLap` | `number` | **(v1.1.42)** Regen média do MGU-H por volta, em MJ. **0 no F1 26** (MGU-H removido). |
| `deployedPctAvgPerLap` | `number` | **"consumo médio"** — % do **teto de deploy** do ano (≤ ~100%). v1.1.42: antes era % do reservatório. |
| `deployedPctPerLap[]` | `number[]` | Deploy por volta, % do teto de regulamento. |
| `harvestedMgukPctAvgPerLap` / `[]` | `number` | Regen do MGU-K, % do teto de harvest (2 MJ 2025 / 8,5 MJ 2026). |
| `harvestedMguhPctAvgPerLap` / `[]` | `number` | Regen do MGU-H, % do reservatório (MGU-H não tem teto por volta; **0 no F1 26**). |
| `deployLimitMjPerLap` / `harvestMgukLimitMjPerLap` / `storeMaxMj` | `number` | **(v1.1.42)** Tetos usados (transparência; permite recomputar %). |
| `deployModeLast` | `string` | Último modo de deploy: `"None"`, `"Medium"`, `"HotLap"`, `"Overtake"`. |
| `samplesCount` | `number` | Total de amostras de `CarStatus` consumidas (~30–35Hz × duração). |
| `samplesPaused` | `number` | Amostras descartadas por `networkPaused=1`. Se for alto, o piloto teve muito lag/pausa. |

#### Como ler na prática

1. **"Quem foi mais agressivo?"** → ordenar por `deployedPctAvgPerLap` desc. Valores ~95–100% indicam ataque máximo todas as voltas; 60–75% indicam economia.
2. **"Quem manteve mais bateria?"** → ordenar por `storePctAvg` desc. ~80% guardador, ~30% gastou tudo o tempo todo.
3. **"Quem regenerou mais?"** → para freadas: `harvestedMgukPctAvgPerLap`. Para turbo (estilo agressivo no acelerador): `harvestedMguhPctAvgPerLap`. **Não some os dois em uma só métrica** — cada fonte tem cap independente de 100% e a soma fica enganadora (foi por isso que removemos `harvestedPctAvgPerLap` na v1.1.35).
4. **"Onde gastou mais?"** → comparar `deployedPctPerLap[i]` entre pilotos para uma volta `i` específica (ex: volta de undercut, primeira após safety car, última volta).
5. **"A captura é confiável?"** → `samplesCount` deve ser ~10 000× minutos de sessão. `samplesPaused` deve ser baixo (< 5% de `samplesCount`). Se `ersTelemetry` for `null`, nenhuma amostra válida foi recebida.

#### Detalhes finos

- **Voltas concluídas vs comprimento dos arrays per-lap.** Os arrays per-lap fecham na detecção de reset do contador (`ersDeployedThisLap` cai abruptamente, indicando cruzamento da linha de chegada). Há uma "in-flight lap" que pode aparecer como uma entrada extra no fim do array (geralmente com valor < 1%, porque é só a cooldown lap pós-bandeirada). Essa entrada extra **não distorce** os agregados (`deployedPctAvgPerLap` etc.) significativamente. Pode ser truncada no consumidor olhando `numLaps` de `results`.
- **Pilotos com `yourTelemetry: "restricted"`.** Em multiplayer público, alguns pilotos podem ocultar telemetria detalhada. Nesses casos, `fuelTelemetry` e `ersTelemetry` podem ficar com valores estranhos (zeros, ranges incompletos). Validar `samplesCount > 0` e `storePctMax > 0` antes de exibir.
- **Spectator mode.** Em modo espectador (`isSpectating: true` na sessão), o plugin recebe ERS de **todos os carros normalmente**. Cobertura típica: 100% dos pilotos.

---

## `sessions[].events[]`

```json
{
  "tsMs": 1778640012345,
  "code": "PENA",
  "name": "PenaltyIssued",
  "data": {
    "penaltyType":          4,
    "penaltyTypeName":      "TimePenalty",
    "infringementType":     7,
    "infringementTypeName": "CornerCuttingGainedTime",
    "vehicleIdx":           5,
    "vehicleTag":           "Drako%",
    "timeSec":              5,
    "lapNum":               3
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

Mapping oficial conforme F1 25 UDP spec (`Data Output from F1 25 v3.pdf`, anexo "Session types"). Corrigido em v1.1.38 — capturas anteriores podem ter labels desatualizados nos IDs 10..14 e 16, mas o `id` numérico sempre é confiável.

| `sessionType.id` | `sessionType.name` | Descrição |
|---|------|-----------|
| 0 | `Unknown` | Tipo desconhecido / sessão não iniciada |
| 1 | `Practice1` | Treino livre 1 |
| 2 | `Practice2` | Treino livre 2 |
| 3 | `Practice3` | Treino livre 3 |
| 4 | `ShortPractice` | Treino curto |
| 5 | `Qualifying1` | Q1 |
| 6 | `Qualifying2` | Q2 |
| 7 | `Qualifying3` | Q3 |
| 8 | `ShortQualifying` | Qualy curta |
| 9 | `OneShotQualifying` | Qualy one-shot |
| **10** | **`SprintShootout1`** | **Quali da Sprint (formato padrão)** |
| 11 | `SprintShootout2` | Quali da Sprint (variante) |
| 12 | `SprintShootout3` | Quali da Sprint (variante) |
| 13 | `ShortSprintShootout` | Quali curta da Sprint |
| 14 | `OneShotSprintShootout` | Quali one-shot da Sprint |
| **15** | **`Race`** | **Corrida principal — única sessão terminal** |
| **16** | **`Race2`** | **Sprint Race (segunda corrida do weekend Sprint Format)** |
| 17 | `Race3` | Terceira corrida (raro) |
| 18 | `TimeTrial` | Time Trial |
| 19, 25, 26, 29, 30, 36 | `Race` | Race em modos online (ranked, Career '25 Online, etc.) |

> O frontend deve sempre tratar `sessionType.name` como a fonte canônica para LABEL DE EXIBIÇÃO. Para LÓGICA crítica (ex.: identificar a Race principal), use combinação `id == 15 || name == "Race"` — assim você cobre tanto F1 25 quanto os modos online sem precisar enumerar todos os IDs.
>
> **Sprint Race vs Race principal:** se quiser separar Sprint Race nos resultados, use `name == "Race2"` ou `id == 16`. A pontuação do campeonato pode então ser calculada considerando ambas (pontos do Sprint + pontos da Race principal).

---

## Status de resultado

| `status` | Descrição | Exibir como |
|--------|-----------|-------------|
| `"Finished"` | Completou a corrida | Mostrar tempo/gap |
| `"DidNotFinish"` | Não terminou (DNF) | "DNF" |
| `"Disqualified"` | Desqualificado (DSQ) | "DSQ" |
| `"NotClassified"` | Não classificado | "NC" |
| `"Retired"` | Abandonou | "RET" |
| `"Inactive"` | Inativo | "—" |
| `"Invalid"` | Volta/sessão inválida | "—" |
| `"Unknown"` | Sem dados | "—" |

---

## Equipes (`teamId` → `teamName`)

**F1 25 (grid de 10 equipes):**

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
| 255 | (My Team) — `teamName == "MyTeam"` |

**F1 26 / "2026 Season Pack" (grid de 11 equipes) — adicionado em v1.1.39:**

| teamId | teamName |
|--------|----------|
| 220 | Mercedes-AMG F1 Team |
| 221 | Scuderia Ferrari HP |
| 222 | Oracle Red Bull Racing |
| 223 | Atlassian Williams F1 Team |
| 224 | Aston Martin Aramco |
| 225 | BWT Alpine F1 Team |
| 226 | Visa Cash App Racing Bulls |
| 227 | MoneyGram Haas F1 Team |
| 228 | McLaren Formula 1 Team |
| 229 | Audi Revolut F1 Team |
| 230 | Cadillac Formula 1 Team |

> Os IDs do F1 26 (220–230) foram confirmados empiricamente (capturas rotuladas), pois a EA não publicou apêndice oficial até 2026-06. O conteúdo F1 26 capturado em **UDP Format 2025** parseia normalmente. Capturas em **UDP Format 2026** ainda NÃO são suportadas (ver `_debug.game.unsupportedUdpFormat`).

---

## Circuitos (`track.id` → `track.name`)

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

O plugin SimHub gera o **mesmo schema base** com as seguintes diferenças:

| Aspecto | App Standalone | Plugin SimHub (≥ v1.1.34) |
|---------|---------------|---------------|
| Container | `.json` puro | `.otk` (AES-CBC + HMAC-SHA256), JSON após decifrar |
| Nome do arquivo | `league_{uid}_{ts}.json` | `{Circuit}_{Date}_{Time}_{Code}.otk` |
| `schemaVersion` | `"league-1.0"` | `"league-1.1"` |
| `drivers[tag].isPlayer` | Não existia | **Novo.** `true` se é o carro do jogador |
| `drivers[tag].driverAssists` | Não existia | **Novo** (TC, ABS) |
| `drivers[tag].fuelTelemetry` | Não existia | **Novo** (combustível) |
| `drivers[tag].ersTelemetry` | Não existia | **Novo** (bateria, em %) |
| `sessions[].lobbySettings` | Não existia | **Novo** (configuração do lobby) |
| `sessions[].isSpectating` | Não existia | **Novo** |
| `sessions[].forecastAccuracy` | Não existia | **Novo** |
| `results[].carIdx` / `raceNumber` | Não existia | **Novo** |
| `results[].classifiedLapped` / `classificationLeaderLaps` | Não existia | **Novo** |
| `results[].numWarnings` / `numDriveThroughPens` / `numStopGoPens` | Presente | **Presente** |
| `safetyCar.lapsUnderSC` / `lapsUnderVSC` | Presente | **Presente** (calculado via interpolação) |

**O frontend deve tratar campos novos como opcionais** (verificar se existem antes de usar), para suportar `.json` antigos do app standalone.

---

## Notas importantes para o frontend

1. **Sempre filtre por `sessionType.name`** para encontrar a corrida/qualy desejada.
2. **`results[]` já vem ordenado** pela classificação original do jogo.
3. **Cruze `results[].tag` com `drivers[tag]`** para obter as voltas detalhadas e telemetria.
4. **`_debug` e `exportDiagnostics` são internos** — não exibir no site. Útil para suporte:
   - `_debug.game` (v1.1.36+) traz `packetFormat`, `gameYear`, `resolvedGameLabel` e `parserMaxSupportedCars`, permitindo identificar de qual versão do jogo veio a captura sem inspecionar os pacotes brutos.
   - `_debug.game` (v1.1.39+) também traz `formatLabel` (rótulo derivado só do `packetFormat` — o formato do fio), `contentPack2026` (`bool` — true quando aparecem equipes 220–230 ou o track 42, indicando conteúdo do F1 26 mesmo em formato de pacote 2025) e `unsupportedUdpFormat` (`int`/`null` — quando não-nulo, a opção "UDP Format" do jogo estava num formato que o parser ainda não suporta, ex.: 2026, e **os dados deste arquivo são não-confiáveis**; o usuário deve voltar para "UDP Format 2025" e recapturar).
   - `_debug.rawSamples` (v1.1.39+) só aparece sob formato não-suportado: traz 1 amostra por `packetId` (`packetFormat`, `length`, `hexPrefix`) usada internamente para mapear o novo layout. Ignorar no frontend.
   - `_debug.integrity.carryOverSessionsDropped` (v1.1.37+) é um `int` que conta quantas "sessões fantasma" foram filtradas pela proteção contra carry-over de Final Classification de uma corrida anterior (caso o usuário tenha iniciado o plugin com a tela de resultados de outra race ainda aberta). Em capturas saudáveis o valor é `0`. Valores `>= 1` significam que a proteção atuou — útil pra orientar o usuário a iniciar o plugin antes do próximo lobby. Quando atua, também aparece em `_debug.notes` uma linha por sessão dropada: `Dropped FC-only carry-over session uid=... drivers=... events=...`.
5. **Tempos em ms** — divida por 1000 e formate com 3 casas decimais.
6. **`isPlayer`** — use para destacar o piloto do usuário nos resultados. Em modo espectador (`isSpectating: true`), nenhum piloto terá `isPlayer: true`.
7. **Carros fantasma são filtrados** — o JSON só contém pilotos reais (Camadas 1–6 da v1.1.29–v1.1.33). Se vier algum, reportar com o `.otk`.
8. **Sessões duplicadas são deduplificadas** — apenas a última de cada tipo é mantida (auto-rotation desde v1.1.31).
9. **Para pilotos humanos online**, a tag pode ser o gamertag. Para IA, é o sobrenome.
10. **`showOnlineNames: false`** indica que o jogador não exibiu o nome real. Para ligas, exija que todos ativem.
11. **`yourTelemetry: "restricted"`** significa que dados privados (`tyreWearPerLap`, `fuelTelemetry`, `ersTelemetry`) podem vir incompletos para esse piloto.
12. **`myTeam: true`** significa `teamName = "MyTeam"`. O frontend deve permitir renomear.
13. **`ersTelemetry`** — a métrica mais útil para análise pós-corrida é `deployedPctAvgPerLap` (consumo) + `storePctAvg` (economia). Cuidado: **não combine `harvestedMgukPctAvg + harvestedMguhPctAvg` em uma única barra "regen"** sem deixar claro ao usuário que cada fonte vai até 100% independentemente. Prefira mostrar duas barras separadas.
14. **F1 25 vs F1 26** (v1.1.36+) — use o campo `game` para chavear branding/lookups: `"F1_25"` = grid de 10 equipes (Mercedes/Ferrari/RBR/Williams/Aston/Alpine/Visa Cash App/Haas/McLaren/Stake Sauber); `"F1_26"` = grid de 11 equipes (adiciona Cadillac; Sauber renomeada para Audi). Equipes ou pilotos novos do F1 26 que ainda não estiverem mapeados aparecerão como `"Team(<id>)"` / `"Driver_<idx>"` — não é bug, é fallback gracioso até a release seguinte do plugin adicionar os IDs reais. **Sempre exiba `teamName`/`tag` recebido**, sem tentar adivinhar nomes corretos no frontend.

---

## Histórico do schema

| schemaVersion | Plugin desde | Mudanças |
|---|---|---|
| `league-1.0` | inicial | Schema original; equivalente ao app standalone (com `isPlayer` adicional). |
| `league-1.1` | **v1.1.34** | Adicionou `ersTelemetry` em cada piloto (campo aditivo). Adicionou também `driverAssists`, `fuelTelemetry`, `lobbySettings`, `isSpectating`, `forecastAccuracy`, `participantsPeakNumActive` e os campos extras em `results[]` (`carIdx`, `raceNumber`, `classifiedLapped`, `classificationLeaderLaps`). |
| `league-1.1` (refinement) | **v1.1.35** | `ersTelemetry.harvestedPctAvgPerLap` (combinado MGU-K + MGU-H) **removido**. Substituído por dois campos separados: `harvestedMgukPctAvgPerLap` e `harvestedMguhPctAvgPerLap` — cada um respeita o cap regulamentar individual de 100% por fonte. Schema string permanece `"league-1.1"` (mudança em campo opcional aditivo). Consumidores que dependiam do agregado antigo devem somar os dois novos. |
| `league-1.1` (refinement) | **v1.1.36** | Preparação para F1 26 (mod do F1 25, mesma base UDP). Campo `game` agora é **dinâmico**, derivado de `PacketHeader.PacketFormat`: `2025 → "F1_25"`, `2026 → "F1_26"`, futuras versões `"F1_<fmt>"`. Novo bloco `_debug.game` com `packetFormat`, `gameYear`, `resolvedGameLabel`, `parserMaxSupportedCars`. Parsers per-car aceitam até 26 entradas (grid F1 26 = 11×2 + wildcards). Schema string permanece `"league-1.1"`. Capturas F1 25 ficam idênticas; novas chaves só aparecem em capturas do F1 26 com IDs ainda não mapeados (que virão como `"Team(<id>)"`/`"Driver_<idx>"` até hotfix de mapping). |
| `league-1.1` (refinement) | **v1.1.37** | Proteção contra "sessão fantasma" carry-over: se o plugin é iniciado enquanto a tela de resultados de outra corrida ainda está enviando pacotes Final Classification, a sessão fantasma resultante (`sessionType=null`, `trackId=null`, drivers `Car_X`) é filtrada antes de chegar ao JSON. Novo campo opcional `_debug.integrity.carryOverSessionsDropped` (`int`). Schema string permanece `"league-1.1"`. Filtro evita o erro "Track(None)" no upload do Race Hub e remove os `Car_X` do `participants[]` global. |
| `league-1.1` (refinement) | **v1.1.38** | **`sessionType.name` labels corrigidos para os IDs 10..14, 16, 17, 18** conforme spec oficial F1 25 (`Data Output from F1 25 v3.pdf`). Antes: `10 → "Race"`, `11 → "Race2"`, `12 → "TimeTrial"`, `13 → "Sprint"`, `14 → "SprintShootout"`, `16 → "Race"`. Depois: `10..14 → "SprintShootout1..3/ShortSprintShootout/OneShotSprintShootout"`, `15 → "Race"` (único terminal), `16 → "Race2"` (Sprint Race), `17 → "Race3"`, `18 → "TimeTrial"`. Schema string permanece `"league-1.1"`. Capturas anteriores (`<= v1.1.37`) NÃO são regeradas — os labels antigos ficam nelas. Para frontend retro-compatível, use `sessionType.id` (sempre canônico) ou normalize labels via tabela acima. Esta correção elimina o bug de Sprint Format gerar múltiplos `.otk` em vez de UM consolidado. |
| `league-1.1` (refinement) | **v1.1.39** | **Suporte ao conteúdo F1 26 ("2026 Season Pack").** `teamId` 220–230 → 11 equipes de 2026 (Audi, Cadillac); `track 42` → Madring. Campo de topo `game` agora considera **conteúdo**: `teamId∈[220,230]` ou `track==42` → `"F1_26"` mesmo com `packetFormat=2025`. Novos campos opcionais em `_debug.game`: `formatLabel`, `contentPack2026`, `unsupportedUdpFormat`. Novo bloco opcional `_debug.rawSamples` (só sob formato UDP não-suportado). **Importante:** capturas em "UDP Format 2026" não são suportadas (corpos de pacote com layout novo) — sinalizadas via `_debug.game.unsupportedUdpFormat`; orientar o usuário a usar "UDP Format 2025". Schema string permanece `"league-1.1"` (tudo aditivo). |
| `league-1.1` (refinement) | **v1.1.40 / v1.1.41** | **Leitura do formato de fio UDP 2026** (Participants/CarStatus/LobbyInfo com layout 2026; roteado por `packetFormat` automaticamente). v1.1.41 promoveu 2026 a suportado → `_debug.game.unsupportedUdpFormat` deixa de aparecer para 2026. **`sessions[].lobbySettings` pode vir `null`** em capturas 2026 (bloco profundo do Session ainda não mapeado nesse formato) — tratar como opcional. Schema string permanece `"league-1.1"`. |
| `league-1.1` (refinement) | **v1.1.42** | **ERS recalibrado para o modelo de energia 2026** (limites FIA: deploy 9 MJ, harvest MGU-K 8,5 MJ por volta no 2026; 4/2 MJ no 2025). Novos campos `ersTelemetry.deployedMjAvgPerLap`/`harvestedMgukMjAvgPerLap`/`harvestedMguhMjAvgPerLap` (MJ absolutos, recomendados) + `deployLimitMjPerLap`/`harvestMgukLimitMjPerLap`/`storeMaxMj`. Campos `%` por volta existentes recalibrados para "% do teto de regulamento" (antes "% do reservatório") — ficam ≤ ~100%. `storePct*` inalterado. Schema string permanece `"league-1.1"`. |

**Política de versionamento:**
- **Bump de minor** (`league-1.0` → `league-1.1`): adição de campos opcionais, retrocompatível para readers que ignoram desconhecidos.
- **Bump de major** (`league-1.x` → `league-2.0`): seria reservado para mudanças que quebrem readers existentes (renomear/remover campo obrigatório, mudar tipo, etc.). Atualmente não planejado.
