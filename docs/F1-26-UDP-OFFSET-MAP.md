# F1 26 (2026 Season Pack) — UDP Format 2026 Offset Map

> **Status (v1.1.40):** Participants (4) e CarStatus (7) **IMPLEMENTADOS** e roteados por formato; LapData/FC/CarDamage/Event e o núcleo do Session já funcionavam. **Pendente:** campos profundos do Session (lobby settings/assists @639+) e LobbyInfo (9) — precisam de captura com `RawSampleHexCap=2048` (entregue na v1.1.40) passando pela tela de lobby. ERS: recalibração de % é follow-up.
> **Origem:** mapa de engenharia reversa (mantido como referência da implementação).
> **Fonte:** captura rotulada `Spa_20260604_195534_7D3526.otk` (gerada na v1.1.39 com **UDP Format = 2026**), via `_debug.rawSamples` (1 amostra crua por packetId, prefixo de 256 bytes).
> **Contexto:** o "2026 Season Pack" roda dentro do F1 25. Com **UDP Format = 2025** tudo já funciona (só faltavam os Lookups, entregues na v1.1.39). Este documento cobre o **formato de fio 2026**, que muda o layout de alguns pacotes.

## 1. Resumo — o que mudou no formato 2026

O **cabeçalho (29 bytes) é idêntico** nos dois formatos (por isso lemos `packetFormat` corretamente). O que muda é o corpo de alguns pacotes. Todos os pacotes por-carro passaram a ter capacidade para **24 carros** (era 22) — para acomodar o My Team como 12ª equipe.

| packetId | Pacote | Tamanho 2025 | Tamanho 2026 | Entrada 2025 | Entrada 2026 | Precisa parser novo? |
|---|---|---|---|---|---|---|
| 0 | Motion | 1349 | 1325 | 60 ×22 | 54 ×24 | Não usamos |
| 1 | Session | 753 | 926 | — | — | **Verificar** (campos iniciais OK) |
| 2 | **LapData** | 1285 | 1399 | 57 ×22 | **57 ×24** | **NÃO — já funciona** |
| 3 | Event | 45 | 45 | — | — | Não (idêntico) |
| 4 | **Participants** | 1284 | 1470 | 57 ×22 | **60 ×24** | **SIM** |
| 5 | CarSetups | 1133 | 1233 | 50 ×22 | — | Não usamos |
| 6 | CarTelemetry | 1352 | 1448 | 60 ×22 | 59 ×24 | Não usamos |
| 7 | **CarStatus** | 1239 | 1445 | 55 ×22 | **59 ×24** | **SIM (só stride)** |
| 8 | **FinalClassification** | 1042 | 1134 | 46 ×22 | **46 ×24** | **NÃO — já funciona** |
| 10 | **CarDamage** | 1041 | 1133 | 46 ×22 | **46 ×24** | **NÃO — já funciona** |
| 11 | SessionHistory | 1460 | 1460 | — | — | Não (idêntico) |
| 12 | TyreSets | 231 | 231 | — | — | Não (idêntico) |
| 13 | MotionEx | 273 | 273 | — | — | Não (idêntico) |
| 15 | LapPositions | 1131 | 1231 | — | — | Não usamos |
| **16** | **NOVO (2026)** | — | 269 | — | — | Investigar (não crítico) |

**Conclusão:** dos pacotes que o nosso pipeline usa, apenas **Participants (4)** e **CarStatus (7)** precisam de ajuste. `LapData`, `FinalClassification` e `CarDamage` têm layout de entrada **idêntico** — só passaram de 22→24 carros, que o parser já suporta (`MaxSupportedCars = 26`). `Session` usa só campos iniciais, que não mudaram.

---

## 2. Participants (packetId 4) — **layout mudou**

Stride por entrada: **57 → 60** (+3 bytes). Capacidade: **22 → 24** entradas. Após o cabeçalho (29) vem 1 byte `numActiveCars`, depois as entradas.

| Campo | Offset 2025 | Offset 2026 | Validação (Spa 2026) |
|---|---|---|---|
| aiControlled | 0 | **0** | igual |
| driverId | 1 | **1** | NORRIS=54, ALONSO=3, SAINZ=0 ✓ |
| networkId | 2 | **2** | igual |
| *(novo)* | — | **3** | sempre `255` (desconhecido) |
| *(novo)* | — | **4** | sempre `255` (desconhecido) |
| **teamId** | 3 | **5** | NORRIS=228(McLaren), ALONSO=224(Aston), SAINZ=223(Williams) ✓ |
| *(novo)* | — | **6** | sempre `1` (desconhecido) |
| myTeam | 4 | **7** | 0 nos AI ✓ |
| raceNumber | 5 | **8** | NORRIS=4, ALONSO=14, SAINZ=55 ✓ |
| nationality | 6 | **9** | NORRIS=10(British), ALONSO/SAINZ=77(Spanish) ✓ |
| **name (32B)** | 7..38 | **10..41** | "NORRIS"/"ALONSO"/"SAINZ" ✓ |
| yourTelemetry | 39 | **42** | =1 (public) |
| showOnlineNames | 40 | **43** | =0 nos AI |
| (techLevel u16) | 41 | **44** | — |
| platform | 43 | **46** | =255 nos AI ✓ |

> Os 3 bytes novos (`[3]`, `[4]`, `[6]`) não são usados pelo nosso parser; ficam documentados como desconhecidos (provavelmente flags do 2026 / livery). O importante (`teamId`, `raceNumber`, `nationality`, `name`, `platform`, `showOnlineNames`) está 100% mapeado e validado.

---

## 3. CarStatus (packetId 7) — **só o stride mudou**

Stride por entrada: **55 → 59** (+4 bytes, anexados no FIM da entrada). Capacidade: **22 → 24**. **Os offsets internos do ERS são IDÊNTICOS ao 2025.**

| Campo | Offset (2025 e 2026) | Validação (Spa 2026, carro 0) |
|---|---|---|
| tractionControl | 0 | =0 |
| fuelInTank (f32) | 5 | =5.70 ✓ |
| fuelCapacity (f32) | 9 | =110.0 ✓ |
| enginePowerIce (f32) | 29 | =406.868 W ✓ |
| enginePowerMguk (f32) | 33 | =0 (início) |
| **ersStoreEnergy (f32)** | 37 | =4.000.000 J (4 MJ cheio) ✓ |
| ersDeployMode | 41 | =3 ✓ |
| ersHarvestedMguk (f32) | 42 | =0 (início) |
| ersHarvestedMguh (f32) | 46 | =0 (início) |
| **ersDeployedThisLap (f32)** | 50 | =9.000.000 J |
| networkPaused | 54 | =0 |
| *(novo 2026)* | 55..58 | 4 bytes (=0 no início; provável Active Aero/Boost/Overtake) |

**Prova de que é só o stride:** o `ersStoreEnergy` do carro 1 lido com **stride 59 = 4.000.000** (correto); com o **stride errado 55 = 0** (lixo). Era exatamente isso que embaralhava o ERS.

> Nota: `ersDeployedThisLap` já marca **9 MJ** enquanto o `ersStoreEnergy` máximo é **4 MJ** — confirma que o modelo de energia 2026 (Boost/Overtake Mode, motor elétrico maior) entrega bem mais energia por volta. Isso impacta a **calibração das porcentagens de ERS** (follow-up separado), não o parsing.

---

## 4. Session (packetId 1) — campos iniciais OK, +173 bytes no fim

Os campos que usamos estão nos **offsets iniciais e não mudaram** (validado na captura de Spa):

| Campo | Offset | Validação |
|---|---|---|
| weather | 0 | =0 (clear) |
| trackTemperature | 1 | =31 °C |
| airTemperature | 2 | =21 °C |
| totalLaps | 3 | =1 |
| trackLength (u16) | 4 | =7007 m (Spa ✓) |
| sessionType | 6 | =9 |
| trackId (int8) | 7 | =10 (Spa ✓) |

Os +173 bytes do 2026 estão **depois** dessa região (forecast/novos campos). **Ação:** revisar `SessionData.cs` para garantir que ele não lê nada além do ponto onde os layouts divergem; se só usa os campos acima, já funciona.

---

## 5. Pacotes que JÁ funcionam no formato 2026 (layout de entrada idêntico)

- **LapData (2):** stride 57 idêntico, 24 carros (+2 bytes finais). Validado: `pos`, `lapNum`, `resultStatus`, tempos coerentes.
- **FinalClassification (8):** row 46 idêntica, 24 carros. Validado: `pos`, `numLaps`, `bestLap` (107.856 ms ≈ 1:47.8, coerente para Spa).
- **CarDamage (10):** 46 idêntica, 24 carros.

Nenhuma mudança de código necessária além de aceitar 24 carros (já temos via `MaxSupportedCars`).

---

## 6. Packet 16 (novo no 2026) — 269 bytes

Pacote novo, enviado **por frame** (mesma frequência de Motion/CarTelemetry/CarStatus). Conteúdo ainda não decodificado — provável telemetria de **Active Aero / Overtake Mode / Boost / energia 2026**. **Não é necessário** para o pipeline atual (resultados de liga). Fica documentado para uma fase futura, se quisermos expor esses dados.

---

## 7. Plano de implementação (Fase 2) derivado deste mapa

1. **`PacketParser.Dispatch`**: ler `header.PacketFormat` e rotear Participants/CarStatus para parsers 2026 quando `>= 2026`.
2. **Participants 2026**: stride 60, 24 slots, offsets da seção 2.
3. **CarStatus 2026**: stride 59, 24 slots (offsets internos idênticos — basta parametrizar o stride).
4. **Session**: confirmar que `SessionData.cs` só usa os offsets iniciais (seção 4).
5. **LapData / FC / CarDamage**: nenhuma mudança (já funcionam).
6. **Remover** o flag `unsupportedUdpFormat` para `2026` quando os parsers estiverem prontos (passa a ser formato suportado em `GameInfo.SupportedParseFormats`).
7. **Testes**: amostras desta captura viram fixtures de regressão (Participants→nomes/equipes corretos; CarStatus→ERS coerente).

> Limitação da amostra atual: prefixo de 256 bytes por pacote cobre o cabeçalho + as primeiras entradas (suficiente para mapear strides e a 1ª entrada). Para validar um carro específico (ex.: o Gasly em carIdx 21) ou os campos finais dos pacotes grandes, aumentar `RawSampleHexCap` e refazer uma captura curta.
