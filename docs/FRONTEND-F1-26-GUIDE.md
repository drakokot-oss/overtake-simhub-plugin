# Guia do Front-end — Suporte ao F1 26 (Pacote Temporada 2026)

> Documento para o time do site/Race Hub. Resume **o que mudou nos arquivos `.otk`** com a chegada do conteúdo do F1 26 e **o que o front precisa fazer** para ler tudo corretamente.
> Schema continua **`league-1.1`** (todas as mudanças são aditivas — nada quebra para arquivos do F1 25).

---

> **Atualização v1.1.41 (2026-06-05):** o formato **UDP 2026 agora é totalmente suportado**. Arquivos capturados em UDP Format 2026 (a partir do plugin v1.1.41) **não vêm mais marcados como experimentais** e podem ser importados normalmente. Veja a seção 4 (atualizada) e a seção 8 (novidades v1.1.41).

## 1. TL;DR — ação rápida

1. **Adicionar os nomes das 11 equipes do F1 26** (`teamId` 220–230) na tabela de equipes do front (seção 2).
2. **Adicionar o circuito de Madri** (`track.id 42` → "Madring").
3. **Ler o novo campo `game`** (pode valer `"F1_26"`) para separar campeonatos 2025 × 2026.
4. **Manter o tratamento do flag `_debug.game.unsupportedUdpFormat`** (seção 4): se vier preenchido, o arquivo é não-confiável → bloquear/avisar. A partir da v1.1.41 ele **não aparece mais** para arquivos 2026 normais (só protege contra plugins antigos / formatos futuros desconhecidos).
5. **Tratar `lobbySettings` como opcional/ausente** em arquivos 2026 (seção 8) — pode vir `null`.
6. **Não assumir teto de 100%** nos campos de ERS de conteúdo F1 26 (seção 8).
7. (Opcional) Para arquivos antigos do F1 26 com `Team(220)` gravado, aplicar fallback numérico (seção 5).

---

## 2. Nomes corretos das equipes (F1 26)

O conteúdo 2026 usa **novos `teamId`s (220–230)**. Confirmados via capturas reais:

| `teamId` | `teamName` (oficial 2026) |
|---|---|
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

A partir do plugin **v1.1.39**, o `.otk` já vem com o `teamName` correto preenchido para essas equipes — o front pode simplesmente **exibir o `teamName` recebido**. A tabela acima serve para: (a) validação, e (b) fallback em arquivos antigos (seção 5).

> Para referência, as equipes do **F1 25** continuam com `teamId` 0–9 (Mercedes-AMG Petronas, Scuderia Ferrari HP, Red Bull Racing, Williams Racing, Aston Martin Aramco, Alpine F1 Team, Visa Cash App Racing Bulls, MoneyGram Haas F1 Team, McLaren Formula 1 Team, Stake F1 Team Kick Sauber). Repare que alguns **nomes mudaram em 2026** (ex.: Mercedes-AMG **F1 Team**, **Oracle** Red Bull Racing, **Atlassian** Williams, **Audi** no lugar da Sauber).

---

## 3. Novo circuito e campo `game`

- **Circuito de Madri:** `track.id = 42`, `track.name = "Madring"`. Único circuito novo do pacote 2026.
- **Campo `game`** (topo do JSON): agora é **content-aware**.
  - `"F1_25"` → conteúdo F1 25.
  - `"F1_26"` → conteúdo F1 26 (detectado por equipes 220–230 **ou** track 42 **ou** formato de pacote 2026).
  - Use esse campo para separar tabelas/campeonatos de 2025 e 2026.

> **Dica:** para lógica robusta, prefira o **id numérico** (`teamId`, `track.id`, `sessionType.id`) como fonte canônica e o `name` apenas para exibição. Ids não mudam; nomes podem ganhar variações de patrocínio.

---

## 4. Flag de formato UDP não-suportado (atualizado v1.1.41)

O jogo tem uma opção **"UDP Format"** (2025 / 2026). **A partir do plugin v1.1.41, os DOIS formatos são suportados** — capturas em UDP 2025 ou 2026 produzem dados corretos.

O flag `_debug.game.unsupportedUdpFormat` continua existindo como **proteção**: ele só fica preenchido quando o arquivo veio de um formato que o plugin **não** sabe ler (ex.: um plugin antigo `< v1.1.41` que gerou um arquivo 2026, ou um formato futuro `2027+`). **Mantenha a regra de bloqueio** — ela protege contra esses casos. Para arquivos 2026 gerados pela v1.1.41+, o flag é `null` e o upload passa normalmente.

Exemplo de um arquivo NÃO-confiável (plugin antigo ou formato futuro):

```jsonc
{
  "game": "F1_26",
  "_debug": {
    "game": {
      "packetFormat": 2026,
      "formatLabel": "F1_26",
      "contentPack2026": false,
      "unsupportedUdpFormat": 2026   // <-- != null => ARQUIVO NÃO-CONFIÁVEL
    },
    "rawSamples": { /* uso interno do plugin (engenharia reversa) */ }
  }
}
```

**O que o front deve fazer no upload:**

```js
const u = otk?._debug?.game?.unsupportedUdpFormat;
if (u != null) {
  // Recusar (ou marcar como inválido) e mostrar mensagem ao usuário:
  // "Este arquivo foi gerado com 'UDP Format 2026', que ainda não é
  //  suportado. No jogo, mude a opção UDP Format para 2025 e gere a
  //  corrida novamente."
  rejectUpload("UDP_FORMAT_2026_UNSUPPORTED");
}
```

Isso evita que corridas com dados embaralhados entrem no sistema. Quando o suporte ao formato 2026 ficar pronto (Fase 2), esse flag deixa de aparecer.

- `_debug.rawSamples` é **uso interno do plugin** (amostras cruas para engenharia reversa). **Ignorar no front.**

---

## 5. Compatibilidade com arquivos `.otk` antigos do F1 26

Arquivos gerados **antes da v1.1.39** (com conteúdo 2026) têm `teamName: "Team(220)"` etc. **gravado dentro do arquivo** — o mapa novo só vale para capturas novas.

Se quiser tornar esses arquivos antigos legíveis sem pedir recaptura, aplique um **fallback numérico** no front: quando `teamName` casar com o padrão `Team(<id>)` e `<id>` estiver em 220–230, substitua pelo nome da tabela da seção 2. Mesma ideia para `Track(42)` → "Madring".

```js
const F1_26_TEAMS = {
  220:"Mercedes-AMG F1 Team", 221:"Scuderia Ferrari HP", 222:"Oracle Red Bull Racing",
  223:"Atlassian Williams F1 Team", 224:"Aston Martin Aramco", 225:"BWT Alpine F1 Team",
  226:"Visa Cash App Racing Bulls", 227:"MoneyGram Haas F1 Team", 228:"McLaren Formula 1 Team",
  229:"Audi Revolut F1 Team", 230:"Cadillac Formula 1 Team",
};
function resolveTeamName(teamId, teamName) {
  const m = /^Team\((\d+)\)$/.exec(teamName || "");
  if (m && F1_26_TEAMS[+m[1]]) return F1_26_TEAMS[+m[1]];
  return teamName;
}
```

---

## 6. Tipos de sessão (lembrete da v1.1.38)

Os labels de `sessionType.name` foram corrigidos conforme a spec oficial. Para a Race principal use `sessionType.id == 15` **ou** `name == "Race"`. A Sprint Race é `id == 16` / `name == "Race2"`. Detalhes completos em `docs/JSON-SCHEMA-GUIDE.md`.

---

## 7. Resumo dos campos novos no `.otk` (v1.1.39)

| Campo | Tipo | Significado |
|---|---|---|
| `game` | `string` | Agora pode ser `"F1_26"` (content-aware). |
| `_debug.game.formatLabel` | `string` | Rótulo derivado só do formato de pacote (pode diferir de `game`). |
| `_debug.game.contentPack2026` | `bool` | `true` se há equipes 220–230 ou track 42. |
| `_debug.game.unsupportedUdpFormat` | `int`/`null` | **≠ null ⇒ arquivo não-confiável** (ver seção 4). A partir da v1.1.41, `null` para arquivos 2026 normais. |
| `_debug.rawSamples` | `object` | Uso interno do plugin. Ignorar. |

Tudo aditivo — arquivos do F1 25 continuam idênticos e nada no front quebra.

## 8. Novidades da v1.1.41 (suporte completo ao UDP 2026)

Com o formato 2026 totalmente suportado, dois pontos de atenção no front:

1. **`lobbySettings` pode vir ausente (`null`) em capturas 2026.** O bloco detalhado de configurações da sala (assists/regras) ainda não é mapeado no formato 2026 e é **omitido** (em vez de mostrar dado incorreto). O front já deve tratar `lobbySettings` como **opcional** — se `null`, simplesmente não exibir essa seção. Todo o resto (resultados, equipes, nomes, ERS, voltas, clima) está presente.

2. **ERS pode passar de 100% em conteúdo F1 26.** O modelo de energia 2026 (Boost/Overtake Mode, motor elétrico maior) entrega mais energia por volta, então `ersTelemetry.deployedPctAvgPerLap` e os `harvested...PctAvgPerLap` **podem exceder 100%**. Não trave a exibição num teto de 100% para esses campos (o `storePctAvg` continua 0–100%). Uma recalibração dessas porcentagens está planejada.

3. **Nomes `Driver_N` continuam possíveis** quando o jogador está com "Show player names: off" (igual ao F1 25). Parte é recuperada pela tela de lobby. Sempre exiba o `tag` recebido.
