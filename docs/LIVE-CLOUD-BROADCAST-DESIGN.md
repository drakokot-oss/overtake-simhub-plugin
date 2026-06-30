# Overtake Live (Cloud Broadcast) — Design / Planejamento

> Status: **planejamento / não implementado**. Este documento mapeia o fluxo,
> arquitetura, multi-tenancy, gating por assinatura e custos ANTES de construir
> qualquer coisa. O modo local atual (servidor web embarcado no plugin) continua
> valendo e não muda. O `.otk` continua 100% local e inalterado.

Última atualização: 2026-06-29

---

## 1. Objetivo

Permitir que **uma pessoa por liga** (narrador / comentarista / admin) que já roda
o plugin coletando a telemetria online **transmita** os dados da corrida para a
nuvem, e que **equipes e engenheiros** acompanhem em tempo real por uma página no
portal da Overtake (`racehub.overtakef1.com`), **restrito a quem assina o Portal
do Piloto** (controle de acesso + monetização).

### Não-objetivos (V1)
- Não substitui o modo local (LAN/OBS) — ele continua existindo.
- Não envia o `.otk` nem muda o pipeline de export.
- Não é controle remoto do PC do transmissor (o plugin só **empurra** dados de saída).
- Não é "telemetria de volantes/pedais bruta" — é o snapshot de corrida (grid,
  gaps, pneus, ERS, combustível, danos, punições, histórico curto de voltas).

---

## 2. Princípios de design

1. **O plugin só faz PUSH de saída** (HTTPS), nunca aceita conexão de entrada →
   funciona atrás de NAT/firewall, sem abrir porta, sem expor o PC.
2. **Opt-in explícito.** O envio para a nuvem é desligado por padrão. A premissa
   "100% offline" do modo local permanece como está.
3. **A nuvem faz o fan-out**, nunca o PC do transmissor. O plugin manda 1 vez; a
   Overtake distribui para N espectadores.
4. **Custo controlado por design:** cadência baixa (1 Hz), payload enxuto
   (mini-histórico de 5 voltas), e o número de mensagens/egress é previsível.
5. **Acesso restrito a assinantes** via Supabase Auth + RLS (canais privados).
6. **Reaproveitar o que já existe:** o `LiveSnapshotBuilder` (gera o JSON) e a UI
   `race-ui.html` (consome WebSocket) são reaproveitados quase como estão.

---

## 3. Personas e fluxo de uso

| Persona | O que faz |
|---|---|
| **Transmissor** (admin/narrador da liga) | Loga no racehub, cria/abre um "evento ao vivo", copia uma **chave de transmissão**, cola no plugin e liga "Transmitir para a Overtake". |
| **Espectador** (engenheiro/equipe, assinante) | Loga no racehub, abre `/live`, escolhe a corrida da sua liga e acompanha em tempo real. |
| **Plataforma** (Overtake) | Valida assinatura/entitlement, faz o fan-out, organiza as salas por liga/evento, monetiza. |

Fluxo resumido:

```
1. Admin cria "Evento ao vivo" no racehub  -> gera broadcast_key (escopo: liga+evento)
2. Admin cola a key no plugin + liga o toggle
3. Plugin valida a key (control plane) e comeca a publicar o snapshot ~1 Hz
4. Espectador assinante abre /live -> ve as corridas ao vivo da(s) sua(s) liga(s)
5. Espectador entra em /live/<evento> -> recebe o estado atual + updates ao vivo
6. Fim da sessao: estado fica "encerrado" ate proxima sessao; admin pode encerrar o evento
```

---

## 4. Arquitetura

Separar **control plane** (autenticação, criar evento, validar assinatura, mint de
token) do **data plane** (o stream de snapshots em alta frequência).

```
                          CONTROL PLANE (baixa frequencia)
 PC do transmissor                                            racehub (front)
 ┌──────────────┐   POST /live/start (broadcast_key)          ┌──────────────┐
 │   Plugin     │ ─────────────────────────────────────►      │  Edge Func   │
 │              │ ◄───────── token de publish + room ──────    │ (valida key, │
 └──────┬───────┘                                              │  entitlement)│
        │                                                      └──────┬───────┘
        │ DATA PLANE (~1 Hz)                                          │
        │ POST /live/publish (snapshot + token)                       │ grava status do
        ▼                                                             ▼ evento (Postgres)
 ┌────────────────────────────────────────────┐            ┌──────────────────┐
 │  Supabase Edge Function (ingest)            │            │  Postgres + RLS  │
 │  - valida token/entitlement, rate-limit     │            │  leagues, events │
 │  - faz downsample/trim se preciso           │            │  subscriptions   │
 │  - realtime.send() -> canal privado         │──────┐     │  broadcast_state │
 │  - upsert ultimo snapshot (late-join)       │      │     └──────────────────┘
 └────────────────────────────────────────────┘      │ fan-out (Realtime Broadcast)
                                                       ▼
                              ┌───────────────────────────────────────────┐
                              │ Espectadores logados+assinantes (browser)  │
                              │ /live/<evento> = a UI race-ui reaproveitada│
                              └───────────────────────────────────────────┘
```

- **Data plane via Edge Function (V1):** o plugin faz `POST` HTTPS a ~1 Hz para
  uma Edge Function que valida e então faz o broadcast no canal privado via
  `realtime.send()`. Vantagens: esconde credenciais, valida entitlement por tick,
  permite rate-limit/downsample, grava o "último estado" para late-join, e
  centraliza a contagem. (Otimização futura: publicar direto no Realtime via REST
  `/realtime/v1/api/broadcast` para cortar invocações de Edge Function — ver §9.)
- **Late-join:** a Edge Function faz `upsert` do último snapshot em
  `broadcast_state`. Quem entra no meio lê 1x esse estado e já vê tudo, depois só
  recebe os deltas/updates do canal.

---

## 5. Por que o Supabase atende (e está disponível no Pro)

Confirmado (Supabase pricing/docs, 2026):
- **Realtime Broadcast** (mensagens efêmeras cliente↔cliente) está disponível no
  Pro e é o mecanismo certo (não precisa gravar cada tick no banco).
- **Canais privados** com **RLS na tabela `realtime.messages`** → dá para amarrar
  o acesso ao canal diretamente à assinatura/entitlement (Realtime Authorization).
- **Auth + Postgres + RLS** já são a base do portal → reaproveita login e regras.
- **Edge Functions** para o control plane e (V1) o ingest.
- **Max message size no Pro = 3 MB** → nosso payload (~8–10 KB) é trivial.

---

## 6. Modelo de dados (Postgres)

Tabelas novas (nomes ilustrativos):

```sql
-- Ligas e membros (provavel que ja exista algo parecido no portal)
leagues(id, name, owner_user_id, ...)
league_members(league_id, user_id, role)   -- role: admin | member | engineer

-- Evento ao vivo (uma corrida/sessao transmitida)
live_events(
  id uuid pk, league_id fk, created_by user_id,
  title, track, session_type, status,        -- status: scheduled|live|ended
  started_at, ended_at, last_seen_at
)

-- Chave que o plugin usa para publicar (escopo liga+evento, revogavel)
broadcast_keys(
  id uuid pk, live_event_id fk, hashed_key, created_by, revoked_at, expires_at
)

-- Cache do ultimo snapshot p/ late-join (1 linha por evento, upsert ~1 Hz)
broadcast_state(live_event_id pk, snapshot jsonb, updated_at)

-- Assinatura do Portal do Piloto (gating de espectador)
subscriptions(user_id pk, plan, status, current_period_end)  -- status: active|past_due|canceled
```

RLS (resumo da intenção):
- **Espectador** pode `SELECT` em `live_events`/`broadcast_state` e **entrar no canal
  privado** do evento **somente se**: é membro da liga **E** tem `subscriptions.status =
  'active'` (ou regra equivalente do Portal do Piloto).
- **Transmissor** pode `INSERT` no canal (enviar broadcast) só para o tópico do seu
  evento, validado pela `broadcast_key` (no V1 quem envia é a Edge Function com
  service role, então a checagem fica na função; nos canais a RLS controla quem lê).

Nomenclatura de canal/tópico (multi-tenant, sem colisão):
```
live:event:{live_event_id}    # uuid global -> isola ligas/corridas entre si
```

---

## 7. Multi-tenancy e concorrência (várias ligas/corridas ao mesmo tempo)

- **1 canal por evento ao vivo.** 4–5 corridas simultâneas = 4–5 canais isolados.
  Não há cross-talk: cada espectador entra só no canal do seu evento.
- **Conexões concorrentes** (limite Pro: 500 incluídas, depois $10/1000):
  - Ex.: 5 corridas × (1 transmissor + 30 espectadores) = **155 conexões** → folgado.
  - Mesmo 10 corridas × 30 = 310 → ainda dentro das 500 incluídas.
- **Compute:** o Realtime roda no compute do projeto. Em concorrência muito alta
  pode ser preciso subir o tamanho do compute (custo à parte das quotas acima) —
  monitorar; não é gargalo nos volumes previstos.
- **Front:** a página `/live` agrupa por liga e lista só os eventos `status='live'`
  a que o usuário tem acesso. Cada card → seu canal. Trocar de corrida = trocar de
  canal (igual ao auto-switch que já fazemos entre quali/corrida).

---

## 8. Payload e cadência (controle de tamanho)

Decisões para a nuvem (diferente do modo local, que pode ser mais "gordo"):
- **Cadência: 1 Hz** (recomendado). Opção 0,5 Hz para visão de engenharia/estratégia.
  (Local segue ~6–7 Hz porque é localhost e de graça.)
- **Mini-histórico: últimas 5 voltas por piloto** (com tempo + delta + setores),
  em vez do histórico completo — exatamente como você sugeriu, e ótimo para
  comparação de engenharia sem inflar o payload.
- **Campos que o engenheiro quer (manter):** posição, gap p/ frente e p/ líder,
  composto+idade+desgaste de pneu, stint/pits, combustível (kg/%/voltas), ERS
  (%+modo), última volta, melhor volta, 5 últimas voltas c/ delta, setores,
  punições (s) + avisos, status.
- **Implementação:** um "perfil cloud" no `LiveSnapshotBuilder` (cap de 5 voltas,
  cadência menor). O builder já existe — é um parâmetro a mais.
- **Tamanho estimado:** ~8–10 KB JSON por snapshot (20 carros, 5 voltas cada).
  Considerar compressão (gzip/permessage-deflate) — reduz egress ~3–4x.

---

## 9. Análise de custo (Supabase Pro, números 2026)

Quotas/preços Pro confirmados:
- **Mensagens Realtime:** 5 M/mês incluídas, depois **$2,50 / 1 M**.
- **Cobrança por fan-out:** cada broadcast = **1 (envio) + 1 por espectador**.
- **Conexões pico:** 500 incluídas, depois **$10 / 1000**.
- **Egress:** 250 GB incluídos, depois **$0,09 / GB**.
- **Edge Functions:** 2 M invocações incluídas, depois **$2 / 1 M**.

### 9.1 Mensagens (o lever principal)
Mensagens por hora de corrida ao vivo = `3600 × cadência × (1 + espectadores)`.

| Cadência | Espectadores | Msgs/hora | Horas de corrida/mês dentro das 5M |
|---|---|---|---|
| 1 Hz | 10 | 39.600 | ~126 h |
| 1 Hz | 20 | 75.600 | ~66 h |
| 1 Hz | 50 | 183.600 | ~27 h |
| 0,5 Hz | 20 | 37.800 | ~132 h |
| 2 Hz | 10 | 79.200 | ~63 h |

> "Horas de corrida/mês" = soma das durações de TODAS as salas ao vivo no mês.
> Ex.: 4 corridas simultâneas de 1,5 h = 6 "horas de corrida" naquele evento.

**Overage é barato e linear.** Ex.: 200 h/mês a 1 Hz com 20 espectadores =
15,1 M msgs → ~10,1 M acima da quota → **~$27,50/mês**. Mesmo um calendário pesado
fica entre $0 e algumas dezenas de dólares.

### 9.2 Egress
Egress/hora ≈ `3600 × cadência × espectadores × payload`.

| Cadência | Espect. | Payload | Egress/hora | Horas dentro de 250 GB |
|---|---|---|---|---|
| 1 Hz | 10 | 8 KB | ~288 MB | ~868 h |
| 1 Hz | 20 | 8 KB | ~576 MB | ~434 h |
| 1 Hz | 20 | 20 KB | ~1,44 GB | ~173 h |

Egress é **menos restritivo** que mensagens nesses tamanhos; compressão e payload
enxuto deixam folga grande. Overage $0,09/GB.

### 9.3 Conexões e Edge Functions
- Conexões: ver §7 — folgado dentro das 500 incluídas.
- Edge Functions (se o ingest passar por função a 1 Hz): 126 h ≈ 453 k invocações
  → dentro das 2 M incluídas. Se crescer muito, **otimização:** publicar direto no
  Realtime (REST broadcast) e usar Edge Function só no control plane.

### 9.4 Conclusão de custo
Com **1 Hz + mini-histórico (5 voltas) + ~10–20 espectadores/corrida**, o plano
**Pro atual cobre dezenas a >100 horas de corrida ao vivo por mês sem custo extra**,
e o overage é linear e barato. O custo escala com **cadência × espectadores ×
payload** — todos sob nosso controle. A monetização do Portal do Piloto cobre
sobra com folga.

---

## 10. Monetização e controle de acesso

- **Espectador:** só acessa `/live/*` e entra nos canais privados com
  `subscriptions.status='active'` (Portal do Piloto) **e** vínculo com a liga.
- **Transmissor:** precisa de permissão (admin/owner da liga) e talvez um plano
  específico para transmitir (definir).
- **Enforcement em 2 camadas:** (1) RLS no canal privado (`realtime.messages`),
  (2) checagem na Edge Function e nas páginas/queries do front.
- **Gancho de billing:** webhook do provedor de pagamento → atualiza
  `subscriptions`. Revogar transmissão = `broadcast_keys.revoked_at`.

---

## 11. Mudanças no plugin (pequenas)

- Nova seção opcional "Overtake Live (nuvem)" nas settings: ligar/desligar, colar
  **broadcast key**, mostrar status (conectado, nº de espectadores se disponível).
- `CloudPublisher`: reusa `LiveSnapshotBuilder` (perfil cloud: 5 voltas, 1 Hz) e
  faz `POST` HTTPS para a Edge Function de ingest com a key/token.
- Mantém o servidor local intacto (os dois modos coexistem).
- Reconexão/retry com backoff; degrada em silêncio se a nuvem cair (não afeta
  captura nem `.otk`).

---

## 12. Organização no front (racehub)

- `/live` (índice): lista as corridas ao vivo a que o usuário tem acesso, agrupadas
  por liga; cada card mostra pista, sessão, volta e nº de espectadores.
- `/live/[eventId]`: a UI de transmissão (a `race-ui.html` portada para o stack do
  portal — provavelmente React/Next), apontando o WebSocket para o canal do evento.
- Área do transmissor: criar evento, copiar broadcast key, iniciar/encerrar.
- Estados de borda: "aguardando início", "ao vivo", "sessão encerrada — aguardando
  próxima" (mesma lógica de persistência que já fizemos no modo local).

---

## 13. Confiabilidade

- **Late-join:** ler `broadcast_state` no join + subscrever updates (máx. ~1 s de
  espera no pior caso a 1 Hz).
- **Reconexão:** cliente e plugin com retry/backoff; canal idempotente (snapshot
  completo cada tick, ou full a cada N s + deltas).
- **Fim de sessão:** manter último estado visível até nova sessão (igual ao local).
- **Watchdog:** se o transmissor sumir (sem ticks por X s), marcar evento como
  "stale/encerrado" no índice.

---

## 14. Segurança

- `wss`/HTTPS sempre; tokens com escopo (só o tópico do evento) e curta validade.
- Service role **nunca** no plugin (fica na Edge Function).
- Canais **privados** com RLS; espectador é **somente leitura**.
- Rate-limit no ingest; broadcast key revogável e com expiração.
- LGPD/consentimento: envio à nuvem é opt-in e comunicado claramente.

---

## 15. Roadmap em fases

- **Fase 0 (agora):** validar o modo local em corridas reais (em andamento). Sem nuvem.
- **Fase 1 — MVP fechado:** Edge Functions (start/publish), canal privado por evento,
  `broadcast_state` p/ late-join, página `/live/<evento>` reusando a UI, gating por
  assinatura, cadência 1 Hz + 5 voltas. Poucas ligas piloto.
- **Fase 2 — produção:** índice `/live` por liga, painel do transmissor, métricas de
  uso/custo, otimização de payload (deltas/compressão), e — se preciso — publicar
  direto no Realtime para cortar invocações de função.
- **Fase 3 — escala:** tiers, limites por plano, observabilidade de custo por liga.

---

## 16. Decisões a confirmar / riscos

1. **Cadência alvo** definitiva (1 Hz vs 0,5 Hz) e nº de voltas no mini-histórico (5?).
2. **Quem paga o quê:** transmissor precisa de plano? espectador-engenheiro paga
   Portal do Piloto? (define o gating exato).
3. **Stack do racehub** (React/Next?) para portar a UI e a página `/live`.
4. **Ingest V1**: Edge Function (controle/validação) vs REST broadcast direto
   (menos invocações) — começar pela função e otimizar depois.
5. **Estimativa real de espectadores/corrida** (10? 30?) para fechar a projeção de custo.
6. **Spend cap:** manter ligado (corta ao exceder) ou desligar (paga overage)?
7. **Limite de conexões** se o número de ligas simultâneas crescer muito (>500 pico).

---

### Referências
- Supabase Pricing: https://supabase.com/pricing
- Realtime Pricing: https://supabase.com/docs/guides/realtime/pricing
- Manage Realtime Messages (cobrança por fan-out): https://supabase.com/docs/guides/platform/manage-your-usage/realtime-messages
- Realtime Authorization (canais privados via RLS): https://supabase.com/docs/guides/realtime/authorization
