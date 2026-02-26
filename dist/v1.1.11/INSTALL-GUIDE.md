# Guia de Instalacao - Overtake SimHub Plugin v1.1.11

## Requisitos

- **Windows 10/11**
- **SimHub** instalado (download gratuito: https://www.simhubdash.com)
- **F1 25** (PC — Steam ou EA App)

---

## Passo 1: Instalar o SimHub (se ainda nao tiver)

1. Acesse https://www.simhubdash.com e baixe o SimHub
2. Execute o instalador e siga os passos na tela
3. Na primeira execucao, o SimHub vai pedir para selecionar os jogos suportados — marque **F1 25**
4. Finalize a configuracao inicial

---

## Passo 2: Instalar o Plugin Overtake

### Opcao A: Instalador automatico (recomendado)

1. **Feche o SimHub** completamente (verifique na bandeja do sistema)
2. Execute o arquivo `Overtake.SimHub.Plugin-v1.1.11-Setup.exe`
3. Se o Windows SmartScreen exibir um aviso, clique em **Mais informacoes** e depois **Executar assim mesmo**
4. O instalador vai detectar a pasta do SimHub automaticamente (geralmente `C:\Program Files (x86)\SimHub`)
   - Se nao detectar, navegue manualmente ate a pasta onde o SimHub esta instalado
5. Clique **Instalar** e aguarde
6. O instalador vai mostrar os proximos passos (ativar o plugin no SimHub)

### Opcao B: Instalacao manual (copiar DLL)

1. **Feche o SimHub** completamente
2. Copie o arquivo `Overtake.SimHub.Plugin.dll` (da pasta `files/`) para a pasta do SimHub:
   - Caminho padrao: `C:\Program Files (x86)\SimHub\`
3. Abra o SimHub novamente

---

## Passo 3: Ativar o Plugin no SimHub

Ao abrir o SimHub apos a instalacao do plugin, ele **detecta automaticamente** a nova DLL:

1. Abra o **SimHub**
2. Um **popup de deteccao** aparecera dizendo que um novo plugin foi encontrado
3. Marque a caixa do **"Overtake F1 25 Telemetry"** e clique **Enable** / **OK**
4. **Reinicie o SimHub** quando solicitado
5. Apos reiniciar, a aba **Overtake Telemetry** aparecera no menu lateral esquerdo

### Se o popup nao apareceu

Se voce abriu o SimHub e o popup de deteccao nao apareceu:

1. Verifique se a aba **"Overtake Telemetry"** ja aparece no menu lateral esquerdo (o plugin pode ja estar ativo)
2. Se nao aparece, feche o SimHub completamente (inclusive na bandeja do sistema / system tray)
3. Confirme que o arquivo `Overtake.SimHub.Plugin.dll` esta na pasta do SimHub (ex: `C:\Program Files (x86)\SimHub\`)
4. Abra o SimHub novamente — o popup devera aparecer

### Se o plugin esta ativo mas a aba nao aparece

Se o plugin foi habilitado mas voce nao ve a aba no menu lateral:
1. **Reinicie o SimHub** completamente (feche pela bandeja do sistema)
2. Ao reabrir, a aba deve aparecer no menu lateral esquerdo

---

## Passo 4: Configurar o F1 25

O jogo precisa estar configurado para enviar dados de telemetria via UDP:

1. Abra o **F1 25**
2. Va em **Settings** > **Telemetry Settings**
3. Configure os seguintes campos:

| Configuracao       | Valor          |
|--------------------|----------------|
| UDP Telemetry      | **On**         |
| UDP Broadcast Mode | **Off**        |
| UDP IP Address     | **127.0.0.1**  |
| UDP Port           | **20777**      |
| UDP Send Rate      | **20Hz**       |
| UDP Format         | **2025**       |

> **IMPORTANTE:** A porta UDP (20777) deve ser a mesma configurada no plugin.

---

## Passo 5: Configuracoes do Plugin

Na aba **Overtake Telemetry** no SimHub:

| Configuracao   | Padrao                           | Descricao                                     |
|----------------|----------------------------------|-----------------------------------------------|
| UDP Port       | 20777                            | Deve ser igual ao configurado no F1 25        |
| Output Folder  | Documents/Overtake Telemetry/output | Pasta onde os JSONs serao salvos           |
| Auto Export    | Ligado                           | Exporta JSON automaticamente ao final da sessao |

---

## Passo 6: Usando o Plugin (Modo Espectador)

1. Abra o **SimHub** e verifique que o plugin esta ativo (aba Overtake Telemetry)
2. Abra o **F1 25** e entre no **lobby multiplayer como espectador**
3. No SimHub, voce vera em tempo real:
   - Status do listener UDP
   - Pacotes recebidos
   - Tipo de sessao, pista, pilotos ativos, sessoes rastreadas
4. **Durante a corrida:** o plugin captura dados automaticamente
5. **Ao final da sessao:** o JSON e exportado automaticamente (se configurado)
6. Para exportar manualmente, clique em **Export League JSON**
7. Para abrir a pasta com os arquivos, clique em **Open Output Folder**

### Dicas para o Modo Espectador

- **Passe a camera por todos os pilotos** nos primeiros minutos — isso ajuda o plugin a identificar todos os participantes
- **Mantenha uma conexao de internet estavel** durante toda a sessao
- **Nao feche o SimHub** durante a sessao de corrida
- **Aguarde pelo menos 30 segundos** apos o final da corrida antes de sair do lobby
- Se a exportacao automatica estiver ligada, o JSON sera gerado 45 segundos apos o fim da corrida para garantir dados completos

---

## Convivencia com Outros Plugins do SimHub

O plugin Overtake funciona de forma independente e **nao conflita** com outros plugins do SimHub. Voce pode usar simultaneamente:

- **SimHub Dash Studio** — dashboards e overlays
- **SimHub ShakeIt** — feedback tatil (bass shakers, buttkickers)
- **SimHub Arduino** — displays e LEDs externos
- **SimHub Wind Simulator** — simuladores de vento
- **Qualquer outro plugin de telemetria** — cada plugin opera em sua propria thread

### Notas de compatibilidade

- O plugin Overtake escuta na porta UDP configurada (padrao: 20777)
- Se outro plugin tambem escuta na mesma porta UDP, pode haver conflito. Nesse caso, altere a porta em um dos plugins
- O SimHub ja recebe telemetria do F1 25 nativamente para dashboards — o plugin Overtake usa um listener UDP separado e **nao interfere** no funcionamento do SimHub
- O plugin nao altera nenhuma configuracao do SimHub ou de outros plugins

---

## Resolucao de Problemas

### O plugin nao aparece no SimHub
- Verifique se o arquivo `Overtake.SimHub.Plugin.dll` esta na pasta correta do SimHub (ex: `C:\Program Files (x86)\SimHub\`)
- Feche o SimHub **completamente** (inclusive na bandeja do sistema / system tray) e abra novamente
- Na primeira abertura apos instalar a DLL, um popup de deteccao deve aparecer — clique "Enable"
- Se o popup nao apareceu mas a aba ja esta no menu lateral, o plugin ja esta ativo
- Se nada acontece, tente deletar o arquivo `Overtake.SimHub.Plugin.dll` da pasta do SimHub, copiar novamente, e reiniciar o SimHub

### "Packets: 0" — nenhum dado recebido
- Verifique se o F1 25 esta com UDP Telemetry ligado
- Confirme que a porta UDP e a mesma no jogo e no plugin (padrao: 20777)
- Verifique se o Windows Firewall nao esta bloqueando o SimHub
  - Abra o Windows Firewall > Permitir um aplicativo > Adicione o SimHub

### JSON exportado com poucos pilotos
- No modo espectador, passe a camera por todos os pilotos durante a sessao
- Verifique se `showOnlineNames` esta **ativado** nas configuracoes de privacidade do F1 25 (caso contrario, os nomes aparecerao como "Player" ou "Player #XX")
- Aguarde a corrida terminar completamente antes de exportar manualmente

### O instalador mostra aviso do SmartScreen
- Isso e normal para aplicativos nao assinados digitalmente
- Clique em **Mais informacoes** > **Executar assim mesmo**
- O instalador apenas copia a DLL para a pasta do SimHub

---

## Estrutura de Arquivos Exportados

Os JSONs sao salvos na pasta de output configurada com o formato:

```
[Pista]_[Data]_[Hora]_[SessionUID].json
```

Exemplo: `Monza_20260225_133122_7277E8.json`

O arquivo segue o schema `league-1.0` e contem:
- Resultados de todas as sessoes (Qualy + Corrida)
- Dados detalhados de cada piloto (voltas, pneus, penalidades, danos)
- Eventos da sessao (Safety Car, penalidades, colisoes)
- Premiacoes (volta mais rapida, mais posicoes ganhas, mais consistente)

---

## Versao

- **Plugin:** v1.1.11
- **Data:** 2026-02-25
- **Schema:** league-1.0
