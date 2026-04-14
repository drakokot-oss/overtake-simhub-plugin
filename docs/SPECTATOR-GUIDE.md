# Guia para Narradores e Comentaristas (Modo Espectador)

Este guia descreve como garantir a captura fiel dos dados quando você usa o Overtake Telemetry como **espectador** (narrador/comentarista) em sessões de corrida.

---

## Por que isso importa

Narradores e comentaristas entram na sessão como **espectadores** — não como pilotos. O jogo F1 25 trata espectadores de forma diferente em alguns aspectos:

- **PlayerCarIndex = 255** — não há "carro do jogador"
- **Troca de câmera** — você alterna entre pilotos durante a transmissão
- **Menus** — pode abrir menus, pausar, etc.

O plugin foi projetado para capturar dados **independentemente** do que está na tela. Porém, algumas práticas ajudam a evitar perda de dados.

---

## Dados que não dependem da câmera

Estes pacotes UDP contêm **todos os carros** e são enviados independentemente do que você está assistindo:

| Pacote | Conteúdo | Impacto |
|--------|----------|---------|
| **Participants (4)** | Nomes, equipes, números de todos os carros | ✅ Completo |
| **LapData (2)** | Voltas, posições, pit stops de todos os carros | ✅ Completo |
| **CarDamage (10)** | Desgaste de pneu, danos de todos os carros | ✅ Completo |
| **Events (3)** | PENA, COLL, SCAR, etc. (broadcast) | ✅ Completo |
| **FinalClassification (8)** | Classificação final | ✅ Completo |

**LapData** inclui `lastLapTimeInMS`, `currentLapNum`, `carPosition` e setores. O plugin usa isso para registrar voltas de **todos** os pilotos, mesmo que você nunca tenha focado neles.

---

## Boas práticas

### 1. Minimize tempo em menu

Quando o jogo está **pausado** ou em menu, ele pode parar ou reduzir o envio de pacotes UDP.

**Recomendação:** Evite pausar ou ficar em menu por longos períodos durante Qualy/Corrida.

### 2. Conecte antes do início

Abra o plugin **antes** de entrar no lobby. Assim, os primeiros pacotes Participants já serão capturados assim que a sessão começar.

### 3. Troca de piloto é segura

Alternar entre pilotos (câmera) **não** afeta a captura de LapData, Participants, Events ou FinalClassification.

### 4. Verifique o JSON exportado

O JSON inclui `_debug.integrity`:

- **`driversWithoutTeam`**: pilotos sem equipe (teamId=255)
- **`isSpectating`**: indica modo espectador

---

## Timing do auto-export

O plugin usa uma lógica de três caminhos para decidir quando exportar:

1. **Caminho principal:** SEND (fim de sessão) + FC já recebido → export em 5 segundos.
2. **Caminho FC tardio:** SEND detectado + FC chega depois → export 5 segundos após o FC.
3. **Fallback 60s:** SEND detectado mas FC nunca chega → export após 60 segundos usando telemetria.

O F1 25 envia o FC (Packet 8) ao exibir a **tela de resultados finais**. Em modo espectador, se a tela de resultados **não carregar** (bug do jogo, desconexão, ou narrador que sai antes), o FC pode nunca ser enviado. Nesses casos o fallback de 60s garante que o export acontece.

### Quando o FC não chega (fallback)

Sem FC, os resultados são reconstruídos a partir da telemetria acumulada. O campo `exportDiagnostics.resultSource` indica a origem:

| Valor | Significado |
|-------|-------------|
| `final_classification` | Resultados do FC oficial do jogo (ideal) |
| `fallback_telemetry` | Resultados reconstruídos a partir de voltas/tempos capturados |

**Limitações do fallback:**
- Posições baseadas em: voltas completadas (DESC) → tempo total (ASC)
- Penalidades de tempo pós-corrida podem não afetar a ordem
- Tempos totais podem ter imprecisão se voltas intermediárias não foram capturadas
- O campo `fcMissingForRace: true` aparece nos diagnósticos quando isso ocorre

**Recomendação:** Aguarde a tela de resultados finais no jogo antes de sair/fechar. Isso garante que o FC seja enviado e os resultados sejam 100% fiéis ao jogo.

## Proteções implementadas

1. **Retenção de teamId** — Não sobrescrevemos teamId válido com 255 (sync/DSQ).
2. **LapData para todos** — Voltas vêm do LapData (22 carros).
3. **Early registration** — Placeholders até Participants chegar.
4. **PlayerCarIndex=255** — Tratamento correto para espectador.
5. **EffectiveLapCount** — Contagem de voltas usa o maior entre `Laps.Count`, maior `LapNumber` e `LastRecordedLapNumber`. Gaps de telemetria em espectador não reduzem a contagem.
6. **Fallback 60s** — Garante export mesmo sem FC.

---

## Resumo

| Ação | Segura? |
|------|---------|
| Trocar de piloto | ✅ Sim |
| Conectar antes do lobby | ✅ Recomendado |
| Aguardar tela de resultados | ✅ **Muito recomendado** |
| Menu por segundos | ⚠️ Evite longos períodos |
| Pausar o jogo | ⚠️ Pode interromper UDP |
| Sair antes dos resultados | ⚠️ FC pode não chegar — fallback é usado |
