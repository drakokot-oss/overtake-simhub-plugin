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

O plugin **só exporta automaticamente após receber o FinalClassification** (Packet 8) da corrida. Dessa forma garante-se que o JSON tenha todos os dados: `tyreStints`, `pitStops`, classificação oficial. O F1 25 envia o Packet 8 ao exibir a tela de resultados. **Se fechar o jogo antes da tela de resultados, não haverá auto-export** — use o botão de export manual.

## Proteções já implementadas

1. **Retenção de teamId** — Não sobrescrevemos teamId válido com 255 (sync/DSQ).
2. **LapData para todos** — Voltas vêm do LapData (22 carros).
3. **Early registration** — Placeholders até Participants chegar.
4. **PlayerCarIndex=255** — Tratamento correto para espectador.

---

## Resumo

| Ação | Segura? |
|------|---------|
| Trocar de piloto | ✅ Sim |
| Conectar antes do lobby | ✅ Recomendado |
| Menu por segundos | ⚠️ Evite longos períodos |
| Pausar o jogo | ⚠️ Pode interromper UDP |
