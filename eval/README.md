# `eval/` — a régua do M1 (busca com ranking híbrido)

> **Piso `L2` revisado (2026-08-11).** Este documento é lido de forma sequencial e várias seções
> abaixo (escritas ANTES da medição, de propósito) tratam `meanPrecision@5 ≥ 0,70` como o piso
> vigente — e ficam exatamente como foram escritas, intactas, porque reescrever análise pré-medição
> para casar com o resultado seria o problema que este projeto existe para evitar. **O piso vigente
> hoje é `L2 ≥ 0,40`**, ratificado via
> `project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md` (repo do harness) depois de a
> T11 reexecutada medir que o melhor ponto do grid elegível a L1/L3 fica em `meanPrecision@5 = 0,41`.
> Racional completo, número antigo, número novo e o porquê: seção "Piso `L2` baixado de 0,70 para
> 0,40 (ADR-005, 2026-08-11)", mais abaixo.

> **`meanPrecision@5` atualizado de 0,41 para 0,42 (2026-08-11, MET-528, ADR-006).** 3 dos 150
> vetores do artefato do corpus (`db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json`) não
> eram reprodutíveis pelo modelo declarado — regenerados e travados por verificação executável
> (`db/seed/README.md`, "Reprodutibilidade do artefato"). A régua remedida com o artefato corrigido:
> `hitRate@3 = 1,00`, `meanPrecision@5 = 0,42` (era 0,41), ordem 2/2, L4 passa — **o piso `L2 = 0,40`
> NÃO mudou** (`floor(0,42 / 0,05) × 0,05 = 0,40`, a mesma regra da ADR-005 aplicada ao valor novo).
> Onde o texto abaixo narra COMO a ADR-005 chegou a 0,40 a partir de 0,41 medido NAQUELE momento, esse
> relato fica intacto — é história real de uma decisão real, não um número desatualizado por descuido.
> Racional completo, tabela antes/depois e o porquê do número ter subido (não foi "afrouxar a régua"):
> seção "Vetores do corpus corrigidos (ADR-006, MET-528, 2026-08-11)", mais abaixo, e
> `project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md` (repo do harness).

Este diretório é o instrumento de medição da MET-479 (`specs/features/met-479-busca-ranking-hibrido/`,
seção **"Medição do Case"**, normativa). Ele existe para uma frase só: *"vazamento no banheiro"
encontra encanador sem a palavra "encanador" aparecer em lugar nenhum* — e esse README documenta como
isso é medido, o que a medição prova e, principalmente, o que ela **não** prova.

## A segunda régua do case (M2): `eval/concurrency-ledger.md`

A partir da MET-480 (M2), este diretório passa a hospedar **duas** réguas nomeadas do case, lado a
lado. Esta primeira (`golden-set.json`, documentada no resto deste arquivo) mede busca/ranking —
que a especialidade certa aparece no topo para uma consulta em linguagem de cliente. A segunda
(`eval/concurrency-ledger.md`, `specs/features/met-480-agendamento-concorrencia/spec.md` seção
"Medição do Case", `tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs`, T15) mede uma coisa
completamente diferente: sob N = 20 tentativas de reserva simultâneas no MESMO horário do MESMO
profissional, com 20 clientes distintos, a defesa de integridade admite **exatamente uma** — as
outras 19 recebem `409`/`slot_conflict`, nunca `503`/`500`/timeout. É a prova de concorrência do M2,
não de qualidade de ranking; as duas réguas não se substituem nem se comparam entre si.

Mesma disciplina das duas: **mudar N, o critério "exatamente 1 sucesso" (C1), "N−1 recusas
409/`slot_conflict`" (C2), "zero qualquer outro status" (C3) ou a exigência de que as três defesas
passem simultaneamente (C4) é ADR + decisão do dono** (`project/adr/ADR-007-ferramenta-do-teste-de-carga.md`,
`project/adr/ADR-008-deadlock-da-exclusao-e-conflito-de-negocio.md`) — nunca edição silenciosa de
`Prumo.Api.Agenda.Scheduling.LoadVerdict` nem do ledger. `eval/concurrency-ledger.md` é
gerado/atualizado pelo próprio teste (mesmo espírito deste README: o número publicado é o que foi
realmente medido, não texto escrito para caber num piso).

## O que a régua mede

Que o **ranking** devolve a **especialidade** certa no topo para uma consulta escrita em linguagem de
cliente (não de catálogo de serviços). Concretamente, três coisas, contra as 20 consultas de
`golden-set.json`:

- **`hit@3`** — há pelo menos um profissional relevante entre os 3 primeiros resultados?
- **`precision@5`** — que fração do top-5 é relevante (denominador `min(5, resultados)`)?
- **Restrições de ordem** (`expectedRankedAbove`) — quando dois profissionais são igualmente
  plausíveis pela semântica, o mais próximo aparece antes do mais distante? É a única asserção da
  régua que distingue **híbrido** (semântica + proximidade) de **só-semântica**.

O cálculo das métricas está em `tests/Prumo.Api.Tests/Eval/EvalMetrics.cs` — código de teste, não de
produto (o "laboratório" não embarca no binário publicado).

## O que a régua NÃO mede

- **Latência.** Nenhum limiar de tempo de resposta é parte desta régua.
- **Qualidade do provedor de embeddings em abstrato.** A régua mede o sistema fim a fim (retrieval +
  ranking), não avalia o modelo de embedding isoladamente contra um benchmark de terceiros.
- **A UI.** Os testes de frontend (Vitest) verificam que a tela renderiza o que a API devolve; esta
  régua roda contra a camada de busca (`Search/`), não contra o navegador.
- **Qual profissional, dentro da especialidade certa, é o melhor.** Ver a limitação de granularidade
  abaixo — é a mais importante das três.

## Limitação declarada: granularidade por especialidade, não por profissional

Um resultado é **relevante** para uma consulta quando a **especialidade** do profissional está em
`expectedSpecialties`. O golden set afirma "o topo tem de ser encanador", não "o topo tem de ser a
Ana". Isso é uma escolha, não um descuido: rotular relevância profissional a profissional em 150
registros sintéticos produziria uma régua mais fina e muito mais frágil — qualquer edição no corpus
invalidaria rótulos — sem mudar o que o case demonstra (spec, "Medição do Case → Julgamento de
relevância").

A única exceção é `expectedRankedAbove`: aí, e só aí, a régua compara dois profissionais nomeados
entre si — e mesmo essa comparação não afirma "A é o melhor profissional em absoluto", só "A deveria
vir antes de B nesta consulta, porque está mais perto e é igualmente relevante".

## Composição do golden set (verificada por teste)

`eval/golden-set.json` tem **20 consultas**, cada uma com `id` estável, `text` em pt-BR de cliente,
`location` (`null` ou coordenadas + `radiusKm` opcional), `expectedSpecialties`, `expectedRankedAbove`
opcional e `notes` explicando por que a consulta existe. `tests/Prumo.Api.Tests/Eval/GoldenSetConformanceTests.cs`
lê o arquivo real (sem banco, sem rede) e falha se qualquer uma destas condições deixar de valer:

- 18–22 consultas no total; `id` e `text` únicos.
- **≥ 12** sem localização, **≥ 5** com localização.
- **≥ 10** consultas cujo texto **não contém** o nome da especialidade esperada nem seus
  `corpusSynonyms` (`db/seed/specialties.json`) — são elas que separam busca semântica de
  `LIKE '%palavra%'`. Hoje o golden set tem **17** consultas nessa condição.
- **≥ 2** consultas com `expectedRankedAbove`.
- A consulta **"vazamento no banheiro"** existe, com `expectedSpecialties: ["encanador"]`.
- Todo slug de `expectedSpecialties` existe em `specialties.json`; todo slug de
  `expectedRankedAbove` existe em `professionals.json` **e** cada par tem dois slugs distintos
  (`a != b` — um par degenerado passaria despercebido na verificação de existência e falharia sempre,
  silenciosamente, no eval).
- Coordenadas em faixa válida (`[-90,90]`/`[-180,180]`); `radiusKm`, quando presente, em `[1,200]`.
- Toda consulta tem `notes` não vazias explicando por que ela existe.

## Sobre a plausibilidade das 20 consultas

Como não existem vetores reais versionados neste repo até a T10 (gate humano — ver "Pendência"
abaixo), a verificação de que cada consulta tem resposta plausível no corpus foi feita por **leitura**
de `db/seed/professionals.json`, consulta a consulta — não por execução do ranking. Cada `notes`
aponta o(s) profissional(is) cuja descrição justifica o resultado esperado. As 17 consultas sem
vocabulário foram cruzadas com as 42 descrições do corpus que a MET-478/T4 escreveu especificamente
sem o vocabulário óbvio da especialidade (ING-08) — é o mesmo conjunto que sustenta a tese do case.

**Importante sobre COMO essa leitura foi feita (revisão da T3):** a verificação de plausibilidade
usou só o texto do corpus e julgamento humano/do agente sobre significado — **nunca** o endpoint local
de embeddings (`Embeddings__BaseUrl=http://localhost:1234`, provedor `openai-compatible` apontando
para um servidor local tipo LM Studio). O modelo servido ali hoje foi verificado e reprovado para
português nesta tarefa: ele casa paráfrase de superfície e erra ponte conceitual — exatamente o
defeito que esta revisão corrige no golden set. Usá-lo para "validar" as consultas teria reintroduzido
o viés que o achado 1 (abaixo) aponta, só que escondido atrás de um número. A régua medida de verdade
(T11) só acontece contra o modelo decidido pelo dono (MET-478/P1), nunca contra esse endpoint local.

### Revisão da T3 (pós-review): ponte conceitual, não paráfrase

O review encontrado nesta janela livre (antes do congelamento) mediu duas propriedades estruturais do
golden set então vigente:

- **Achado 1 — um baseline puramente lexical (o análogo de `LIKE '%palavra%'`) atingia
  `hitRate@3 = 1,00`** neste conjunto. Rodando o `LexicalBaseline` que esta mesma revisão passa a
  versionar (`tests/Prumo.Api.Tests/Eval/LexicalBaseline.cs`) contra o golden set antigo
  (`git show 86f8ee6:eval/golden-set.json`), o `hitRate@3 = 1,00` se reproduz exatamente; o alvo
  aparecia em **1º lugar** (não só no top-3) em **17 das 20** consultas — as três exceções são `gs-01`
  (1º lugar era um técnico em ar-condicionado, "vazamento de gás refrigerante"), `gs-06` (1º lugar era
  uma diarista) e `gs-16` (1º lugar era um eletricista). As consultas cumpriam a letra do critério "sem
  vocabulário da especialidade" (BSC-13), mas 15 das 20 eram **paráfrase quase literal** de uma
  descrição específica do corpus — ex.: a consulta original de `gs-03` ("a luz da cozinha fica
  piscando sem motivo aparente") reusava quase palavra por palavra a descrição de Rodrigo Costa
  ("a luz da cozinha fica piscando sem motivo"). Isso deixava a régua incapaz de distinguir busca
  semântica de correspondência literal de palavra — exatamente a tese que o case afirma provar.
- **Achado 2 — teto aritmético de `meanPrecision@5` = 0,82** contra o piso `L2 = 0,70` da spec, uma
  consequência estrutural da composição (localização + `MinSemanticScore = 0`), não do ranking. Ver
  "Teto estrutural de `meanPrecision@5`" abaixo.

O dono decidiu, e esta revisão da T3 aplicou:

1. **Reescrita das 16 consultas "sem vocabulário" afetadas** (todas exceto `gs-01`, protegida por ser
   o DoD literal da issue) para **ponte CONCEITUAL**: zero sobreposição de palavra de conteúdo
   (substantivo, verbo específico, adjetivo — ignorando artigos, preposições, pronomes e verbos vazios
   como "ser/estar/ter/fazer/ficar") entre a consulta e **a descrição do profissional-alvo citado em
   `notes`** — não contra as outras 9 descrições da mesma especialidade no corpus, que ninguém
   verificou palavra a palavra. Verificado por script sobre o texto normalizado (mesma técnica NFD/sem
   acento/minúscula do resto do projeto) contra a descrição real citada, em
   `db/seed/professionals.json`, antes da revisão ser aceita — não por leitura visual sozinha.
   Em 6 dessas 16 consultas (`gs-07`, `gs-08`, `gs-09`, `gs-12`, `gs-13`, `gs-14`) o profissional-alvo
   citado também mudou junto com o texto (a `notes` de cada uma registra a troca); medindo a consulta
   reescrita contra as **10** descrições da especialidade esperada (não só a citada), sobra palavra
   distintiva em algumas — `gs-04` mantém "quadro" (presente em `rafael-silva-vre-012`), `gs-05`
   mantém "acabamento", `gs-08` mantém "verde", `gs-12` mantém "academia" (literal na descrição do
   alvo *antigo*, `viviane-costa-nit-098`), `gs-14` mantém "varanda" e o radical `embaç-`, `gs-18`
   mantém "salão". Isso não viola o item 1 como o dono o definiu (a régua é "zero contra o alvo citado
   em `notes`", não "zero contra a especialidade inteira") e **não é um problema escondido**: esse
   resíduo especialidade-a-especialidade é exatamente uma das coisas que o baseline lexical (ver
   "Baseline lexical" abaixo) mede e expõe — é parte de por que ele acerta 11 das 20 consultas em vez
   de 0. Um golden set cujo baseline lexical desse `hitRate@3 = 0,00` seria suspeito na direção oposta:
   sugeriria consultas artificialmente "anti-palavra" em vez de linguagem natural de cliente.
2. **Ambiguidade de especialidade concorrente declarada** em `notes`, nas 4 consultas onde o review
   achou uma leitura plausível para outra especialidade (`gs-06` pintor vs. diarista; `gs-08`
   jardineiro vs. dedetizador; `gs-10` marceneiro vs. montador-de-moveis; `gs-14` vidraceiro vs.
   diarista) — cada `notes` nomeia a especialidade concorrente e explica por que o esperado é o
   esperado.
3. **`gs-16` e `gs-17` diversificadas**: usavam o mesmo ponto (centroide de Belo Horizonte) e o mesmo
   par de cidades (BH → Contagem), o que as fazia tender a passar ou falhar juntas sob o mesmo `τ`.
   `gs-17` passou a usar um ponto (centroide do Rio de Janeiro) e um par de cidades (Rio de Janeiro →
   Niterói) diferentes de `gs-16`, com `radiusKm` também diferente (15&nbsp;km em vez de 20&nbsp;km).
4. **Centroides corrigidos** em `gs-18` (Petrópolis, estava a ~247&nbsp;m do centroide real),
   `gs-19` (Uberlândia, ~106&nbsp;m) e `gs-20` (Volta Redonda, ~199&nbsp;m) — recalculados como a média
   exata das coordenadas dos profissionais de cada cidade no corpus (mesma técnica de design.md §3.5:
   `GroupBy City/State`, `Average(Latitude)`, `Average(Longitude)`).
5. Esta seção e "Teto estrutural de `meanPrecision@5`"/"Ordem de diagnóstico" abaixo, registradas
   **antes** de qualquer medição real (T11 ainda não rodou).
6. **Baseline lexical publicado** — ver "Baseline lexical" abaixo.

## Teto estrutural de `meanPrecision@5` (declarado antes da medição)

**Isto não é uma medição — é aritmética sobre a composição do golden set**, registrada agora
(revisão da T3), antes de a T11 rodar o eval de verdade, para que um resultado abaixo do piso L2 não
seja lido como "o ranking está ruim" quando pode ser simplesmente "a composição do golden set não
permite mais do que isso".

Com `Ranking:MinSemanticScore = 0` (o default até a calibração — corte desligado, spec D3), o
denominador de `precision@5` é `min(5, n)`, onde `n` é o número de candidatos que sobrevivem ao filtro
geográfico (D5: distância ≤ `min(service_radius_km, radiusKm do cliente)`) — nenhum filtro de
especialidade existe nessa camada, então `n` inclui profissionais de **todas** as especialidades
dentro do raio, não só a esperada. O teto de `precision@5` por consulta é
`min(5, n, relevantes_alcançáveis) / min(5, n)`.

Para as **15 consultas sem localização**, o filtro geográfico não existe (D4): `n` é o corpus inteiro
(150), e cada especialidade tem exatamente 10 profissionais no corpus — bem acima de 5 — então o teto
por consulta é `min(5, 10) / min(5, 150) = 5/5 = 1,00` (alcançável em tese, com ranking perfeito).

Para as **5 consultas com localização** (composição final desta revisão — recalculado, os números
mudam com a reescrita de `gs-16`/`gs-17`/`gs-18`/`gs-19`/`gs-20`), a geografia restringe `n` a poucas
dezenas de profissionais de especialidades variadas, e a especialidade esperada quase sempre tem só 1
ou 2 representantes dentro do raio:

| consulta | especialidade | candidatos no raio (`n`) | relevantes alcançáveis | teto `precision@5` |
|---|---|---:|---:|---:|
| `gs-16` | encanador | 18 | 2 | `min(5,18,2)/min(5,18)` = 2/5 = **0,40** |
| `gs-17` | montador-de-moveis | 16 | 2 | `min(5,16,2)/min(5,16)` = 2/5 = **0,40** |
| `gs-18` | cabeleireiro | 8 | 1 | `min(5,8,1)/min(5,8)` = 1/5 = **0,20** |
| `gs-19` | manicure | 9 | 1 | `min(5,9,1)/min(5,9)` = 1/5 = **0,20** |
| `gs-20` | dedetizador | 8 | 1 | `min(5,8,1)/min(5,8)` = 1/5 = **0,20** |

Somando os 20 tetos por consulta (15 × 1,00 + 0,40 + 0,40 + 0,20 + 0,20 + 0,20 = 16,40) e dividindo por
20:

```
teto meanPrecision@5 = 16,40 / 20 = 0,82
```

**Isto é o teto MÁXIMO possível, não uma previsão** — ele assume ranking perfeito (todo relevante
alcançável aparece no topo antes de qualquer irrelevante). Ele é o mesmo valor (0,82) que o review
mediu contra a composição anterior: a reescrita da T3 corrigiu o **achado 1** (paráfrase), não o
**achado 2** (teto aritmético) — os dois são problemas independentes, e só o dono decide se o segundo
precisa de correção (mudar composição/raio é ajuste de golden set, sujeito à mesma regra de "só antes
do congelamento, com motivo registrado"; a T3 não alterou raios/pontos por essa razão, só diversificou
`gs-16`/`gs-17` e corrigiu os 3 centroides errados, conforme mandado).

Consequência aritmética direta: como o teto de 0,82 já está acima do piso L2 = 0,70, ainda existe
margem — mas ela está toda nas 15 consultas sem localização: para o `meanPrecision@5` **medido** (não
o teto) alcançar 0,70, as 15 consultas sem localização precisam mediar `precision@5 ≥ 0,84`
(`(0,70 × 20 − 1,40) / 15 = 0,84`), já que as 5 consultas com localização não podem contribuir mais que
seus tetos (0,40/0,40/0,20/0,20/0,20 = 1,40 no total). Isso não é um piso novo — é a mesma régua L2 da
spec, só com a aritmética explícita para que a T11 saiba, antes de medir, onde a margem real está.

> **Nota de encaminhamento:** esta seção inteira é pré-medição e trata 0,70 como o piso vigente de
> propósito — preservada intacta. A T11 mediu que a margem projetada aqui não se concretizou
> (`meanPrecision@5` ficou em 0,39–0,41 em todo o grid, não perto de 0,84 nas consultas sem
> localização); o piso vigente hoje é `L2 ≥ 0,40` (ADR-005) — ver "Piso `L2` baixado de 0,70 para 0,40"
> mais abaixo para a medição real e o porquê.

## Ordem de diagnóstico se L2 falhar (fixada antes de qualquer medição)

Declarada agora, **antes** de a T11 rodar o eval contra vetores reais, para que uma medição abaixo do
piso não vire desculpa para "ajustar um peso até passar" (o que a spec já proíbe sem ADR). Se
`meanPrecision@5` medido ficar abaixo do piso L2 = 0,70 em qualquer ponto do grid, a ordem de
investigação é esta, nesta ordem, e não outra:

1. **O modelo de embeddings primeiro.** É o fator com maior variância desconhecida (nenhum vetor real
   existe neste repo até a T10) e o mais barato de descartar/trocar sem tocar na régua (MET-478/P1 já
   prevê essa possibilidade). Perguntas: o modelo captura similaridade semântica em português coloquial
   (não só formal)? Ele diferencia as 15 especialidades do corpus de forma estável? Um modelo que casa
   paráfrase mas erra ponte conceitual (como o endpoint local verificado e reprovado nesta revisão —
   ver "Sobre a plausibilidade") reproduziria exatamente um `meanPrecision@5` baixo sem o ranking ter
   culpa nenhuma.
2. **A densidade do corpus por especialidade×cidade, depois.** O teto estrutural acima já mostra que
   consultas com localização têm só 1–2 profissionais relevantes alcançáveis dentro do raio — se o
   modelo estiver correto e a medição ainda ficar abaixo do piso, o próximo suspeito é o corpus ser
   geograficamente esparso demais para as consultas com localização (poucos profissionais da mesma
   especialidade perto o bastante uns dos outros para o ranking ter margem de decidir por proximidade).
3. **Só então a fórmula** (pesos, `τ`, corte) — e mesmo aí, mudar qualquer um deles depois do
   congelamento é ADR nova que supersede a ADR-003 (spec, "Congelamento"); antes do congelamento, é a
   T11 que varre o grid declarado e escolhe pela regra publicada, nunca um agente "tentando valores até
   passar".

Nesta ordem porque o ranking (item 3) é a peça mais barata de culpar e a mais cara de trocar depois de
publicada — e é exatamente por isso que ela vem por último, não por primeiro.

## Baseline lexical (referência publicada ao lado de semântica e híbrido)

Por decisão do dono (revisão da T3), o número do baseline lexical — o análogo de
`WHERE description ILIKE '%palavra%'` somado por termo da consulta, aplicando o **mesmo filtro
geográfico** de D5 — é publicado ao lado dos números de só-semântica e híbrido que a T11 vai preencher
abaixo. O instrumento vive em `tests/Prumo.Api.Tests/Eval/LexicalBaseline.cs` (scorer) e
`tests/Prumo.Api.Tests/Eval/LexicalBaselineConformanceTests.cs` (a asserção contra o golden set real) —
código de teste, não de produto, mesma natureza de `EvalMetrics` (design.md §8.2): nunca embarca no
binário publicado.

A única asserção normativa é estrutural, não numérica: **o baseline lexical não pode atingir
`hitRate@3 = 1,00`** no golden set — se atingisse, o conjunto não estaria discriminando busca semântica
de correspondência literal de palavra (o inverso exato do achado 1). Não há piso numérico inventado
aqui (`≤ 0,45` ou qualquer outro valor) — isso seria régua nova, e régua é ADR + decisão do dono; os
números completos do baseline lexical, lado a lado com só-semântica e híbrido, são publicados pela T11.

Dito isso, como o cálculo do baseline lexical é determinístico sobre o texto do golden set (não
depende de nenhum provedor de embeddings), o número já pode ser medido a qualquer momento, sem vetor
nenhum. Medido originalmente pela revisão da T3: o baseline lexical errava 9 das 20 consultas no
top-3 (`hitRate@3 = 0,55`).

**Recalculado nesta revisão (MET-524), depois de `gs-07` ser reescrita** — o baseline lexical não
depende de vetor, então a mudança de `gs-07` muda esse número: agora o baseline lexical erra **8**
das 20 consultas no top-3 (`hitRate@3 = 0,60`, 12/20) — falhas em `gs-03`, `gs-09`, `gs-10`, `gs-14`,
`gs-17`, `gs-18`, `gs-19`, `gs-20`. Ainda abaixo do piso L1 = 1,00 que a busca semântica precisa
cumprir, e ainda a folga que torna o conjunto discriminante — mas a folga diminuiu (de 9 para 8
falhas), porque a `gs-07` nova é agora acertada pelo baseline lexical, e vale registrar por quê:
**não é sinal lexical genuíno**, é desempate. `gs-07` empata em contagem de palavra de conteúdo (2)
entre vários candidatos de especialidades diferentes (`carlos-duarte-rp-108`,
`fernando-duarte-pet-028`, `gabriel-freitas-ubl-038`, entre outros — nenhum deles compartilha mais de
2 palavras de conteúdo com a consulta), e o desempate por `slug` (ordem alfabética, mesma disciplina
de ordem total do `LexicalBaseline`) coloca `gabriel-freitas-ubl-038` (diarista) na 3ª posição do
top-3, ao lado de `carlos-duarte-rp-108` (professor-particular) e `fernando-duarte-pet-028` (pintor)
nas duas primeiras. Ou seja: a revisão de `gs-07` (motivada por construção do enunciado, não pela
régua — ver ADR-004) teve um efeito colateral mensurável, reduzindo marginalmente a discriminação do
golden set contra correspondência literal de palavra. Não invalida a consulta (a ponte continua
conceitual, zero sobreposição de palavra com a descrição-alvo real) nem quebra a asserção estrutural
desta seção (`hitRate@3 = 0,60 < 1,00`), mas fica registrado — a régua não se ajusta escondendo o que
mudou. Este número é informativo (prova a propriedade que a asserção do teste exige); o valor
oficial, ao lado de semântica e híbrido na mesma tabela, é o que a T11 publica.

## Grid de calibração e limiares — MEDIDO (T11 reexecutada, MET-524; artefato do corpus corrigido, MET-528/ADR-006); régua fecha com `L2` revisado (ADR-005)

**Medição real** (reexecutada em 2026-08-11 para esta revisão, MET-528),
`tests/Prumo.Api.Tests/Integration/GoldenSetEvalTests.cs`, contra Postgres + pgvector real
(Testcontainers), corpus semeado com os vetores REAIS de
`db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json` (não `hashing` — artefato corrigido
pela MET-528, ver "Vetores do corpus corrigidos" abaixo), consultas vetorizadas pelo MESMO
`SearchQueryEmbedder`/`PrecomputedEmbeddingStore` que a API usa, candidatos recuperados por
`ProfessionalSearchQuery` (a mesma consulta SQL da API) e re-ranqueados pelos 18 pontos com
`HybridRanker.Rank` — a mesma camada de busca, ponta a ponta, exceto o transporte HTTP. As métricas
são calculadas sobre a lista TRUNCADA em `Search:DefaultResultLimit` (10) — a mesma lista que a API
devolveria a um cliente que não informa `limit`.

Esta é a **terceira** execução real deste eval contra vetores reais. A primeira (2026-08-10, `bge-m3`,
branch `met-479-t11`, não mesclada) reprovou com `hitRate@3 = 0,75` (falhas: `gs-05`, `gs-07`,
`gs-09`, `gs-14`, `gs-18`) e `meanPrecision@5` entre 0,39 e 0,42. Seguindo a ordem de diagnóstico
pré-comprometida abaixo (modelo primeiro), o dono decidiu trocar de modelo (`qwen3-embedding-0.6b`) e
revisar `gs-07` por um defeito de construção — `project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md`
(repo do harness). A segunda (MET-524, também 2026-08-11) mediu o resultado dessa troca — uma única
vez, sem segunda rodada de ajuste (disciplina da própria MET-524) — e é a medição que a tabela abaixo
mostrava até esta revisão: `hitRate@3 = 1,00` no ponto configurado, `meanPrecision@5 = 0,41` no único
ponto elegível. A **terceira** (esta revisão, MET-528) reexecuta a MESMA suíte, sem tocar peso, `τ`,
corte, golden set nem código de ranking — só o artefato do corpus mudou (3 dos 150 vetores,
irreprodutíveis pelo modelo declarado, corrigidos — ver "Vetores do corpus corrigidos" abaixo).

⛔→✅ **Resultado da medição: `hitRate@3 = 1,00` no ponto configurado (L1 e L4 fecham) e L3 é
alcançável em um único ponto do grid, mas NENHUM ponto do grid satisfaz `meanPrecision@5 ≥ 0,70` (o
piso ORIGINAL da spec)** — o melhor `meanPrecision@5` entre os pontos elegíveis (que satisfazem L1 e
L3 juntos) é **0,42** (era 0,41 antes da correção de vetores da MET-528 — mesmo ponto, mesma ordem,
mesmo `hitRate@3`; só `meanPrecision@5` mudou), bem abaixo de 0,70. Diante disso a T11 original
**parou e reportou** — exatamente o que `spec.md:163-165` manda quando o melhor ponto do grid fica
abaixo do piso: "o dono decide... ou ratifica um limiar menor via ADR. Nenhum agente escolhe". Nenhum
peso foi escolhido por este agente, nenhum piso foi alterado por este agente, nada em
`golden-set.json` foi tocado — nem pela T11 original, nem por esta revisão (MET-528).

**O dono decidiu** (2026-08-11, fora desta task, registrado em
`project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md` no repo do harness): ratificar
um piso menor, derivado da MESMA regra aritmética que a spec já usa para o caso simétrico de subir o
piso (`spec.md:161`, arredondar para baixo em passos de 0,05), aplicada ao valor medido no único ponto
elegível. Ver "Piso `L2` baixado de 0,70 para 0,40", logo após a tabela, para o número antigo, o novo
e o porquê — e "Vetores do corpus corrigidos (ADR-006, MET-528)", logo depois, para por que o piso
continua o mesmo mesmo com o número subindo de 0,41 para 0,42. A tabela abaixo é a saída real e
completa do teste (nenhum ponto foi omitido), medida com o artefato do corpus CORRIGIDO (MET-528):

| `semanticWeight` | `distanceDecayKm` | `hitRate@3` | `meanPrecision@5` | ordem 100%? | L1&L3? |
|---|---|---|---|---|---|
| 1,0 | 5  | 1,00 | 0,42 | não (1/2) | não |
| 1,0 | 10 | 1,00 | 0,42 | não (1/2) | não |
| 1,0 | 20 | 1,00 | 0,42 | não (1/2) | não |
| **0,9** | **5** *(adotado — appsettings.json)* | **1,00** | **0,42** | **sim (2/2)** | **sim** |
| 0,9 | 10 | 0,95 | 0,42 | sim (2/2) | não |
| 0,9 | 20 | 0,95 | 0,42 | sim (2/2) | não |
| 0,8 | 5  | 1,00 | 0,41 | não (1/2) | não |
| 0,8 | 10 | 1,00 | 0,41 | não (1/2) | não |
| 0,8 | 20 | 0,95 | 0,42 | sim (2/2) | não |
| 0,7 | 5  | 0,95 | 0,41 | não (1/2) | não |
| 0,7 | 10 | 1,00 | 0,41 | não (0/2) | não |
| 0,7 | 20 | 1,00 | 0,41 | não (1/2) | não |
| 0,6 | 5  | 0,95 | 0,41 | não (0/2) | não |
| 0,6 | 10 | 0,95 | 0,41 | não (0/2) | não |
| 0,6 | 20 | 1,00 | 0,41 | não (0/2) | não |
| 0,5 | 5  | 0,85 | 0,40 | não (0/2) | não |
| 0,5 | 10 | 0,95 | 0,41 | não (0/2) | não |
| 0,5 | 20 | 0,95 | 0,41 | não (0/2) | não |

Todo `meanPrecision@5` da tabela subiu exatamente 0,01 em relação à medição anterior (0,41→0,42 no
ponto adotado, 0,40→0,41 nos demais, 0,39→0,40 no pior ponto) — nenhum `hitRate@3` nem nenhuma coluna
de ordem mudou. É a assinatura esperada de corrigir 3 vetores em 150 (2% do corpus): um efeito
pequeno, uniforme e não seletivo — não um ponto específico "melhorando mais" que os outros, o que
seria suspeito de coincidência com a escolha do ponto adotado.

Só **um** dos 18 pontos (`w_s=0,9 / τ=5`) satisfaz L1 e L3 simultaneamente — o subconjunto elegível
da regra de escolha da spec não está mais vazio como na medição contra `bge-m3`, mas o único elegível
mede `meanPrecision@5 = 0,42`, abaixo do piso ORIGINAL de 0,70 da spec. A regra de calibração do L2
para SUBIR o piso ("o piso medido pode subir, nunca descer") não se aplica aqui: o número medido no
único ponto elegível está abaixo do piso fixado pela spec, não acima dele — é o outro ramo que a
própria spec já previa (`spec.md:163-165`) e que motivou a decisão do dono documentada logo abaixo.

Para referência (baseline lexical, determinístico, sem vetor nenhum — não depende de embedding, logo
inalterado pela correção de vetores da MET-528 — recalculado contra o golden set desta revisão — ver
"Baseline lexical" acima): `hitRate@3 = 0,60` (baseline lexical) < `0,85`–`1,00` (grid
semântico/híbrido medido agora) — a folga entre busca semântica e correspondência literal de palavra
ficou ainda maior com o modelo novo do que estava com `bge-m3` (`0,75`–`0,80`).

**Só-semântica vs. híbrido:** `semanticWeight = 1,0` (só-semântica) alcança `hitRate@3 = 1,00` nos 3
pontos de `τ`, mas falha L3 (1/2) nos três — sem proximidade no score, os dois pares de
`expectedRankedAbove` (`gs-16`, `gs-17`) não têm como ser desempatados por distância. O único ponto
elegível (`0,9/5`) já é híbrido, com peso de proximidade pequeno (0,1) — é o suficiente para resolver
a ordem sem alterar `hitRate@3`.

### Piso `L2` baixado de 0,70 para 0,40 (ADR-005, 2026-08-11)

> Esta seção narra a decisão exatamente como ela foi tomada, no momento em que foi tomada — o valor
> medido então era `meanPrecision@5 = 0,41`, e é esse número que aparece abaixo, intacto, porque foi
> com ele que a ADR-005 derivou o piso. **O valor medido HOJE é 0,42** (3 vetores do corpus corrigidos
> pela MET-528/ADR-006, depois desta decisão — ver "Vetores do corpus corrigidos", logo abaixo) — a
> nota ao final da derivação da fórmula, e ao final da seção "O tamanho real da folga", apontam
> explicitamente para o número atual. O piso `L2 = 0,40` **não mudou**.

**Pesos escolhidos: `semanticWeight = 0,9`, `proximityWeight = 0,1`, `distanceDecayKm (τ) = 5`.** A
regra de escolha da spec (`spec.md:177-182`: "descartar pontos que violem L1 ou L3 → maior
`meanPrecision@5` → empate...") tem, desta medição, exatamente **um** sobrevivente ao primeiro filtro
(`0,9/5`) — não há empate a resolver, a regra termina no primeiro passo. `appsettings.json:Ranking`
foi atualizado com estes valores (era 0,7 / 0,3 / 10 / 0,0 — os provisórios da T1); o comentário
"provisório" foi removido e substituído pela referência à medição e à ADR-005.

**Limiar `L2`: RATIFICADO em 0,40 — piso ANTIGO 0,70, piso NOVO 0,40, decisão do dono via ADR, nunca
em silêncio.** O único ponto elegível a L1/L3 mede `meanPrecision@5 = 0,41`. Como esse valor fica
ABAIXO do piso original (não acima), a regra de calibração do L2 para SUBIR não se aplica; o cenário é
o outro que a spec já previa (`spec.md:163-165`): "se o melhor ponto ficar abaixo de 0,70... ratificar
um limiar menor via ADR. Nenhum agente escolhe." O dono ratificou, em
`project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md` (repo do harness), um piso
NOVO derivado pela MESMA regra aritmética que a spec usa para o caso simétrico de subir o piso
(`spec.md:161`, arredondar para baixo em passos de 0,05) aplicada ao valor medido:

```
L2 = floor(0,41 / 0,05) × 0,05 = 0,40
```

**Nota (2026-08-11, MET-528, ADR-006) — o piso não se move com o número novo.** Depois desta decisão,
3 dos 150 vetores do corpus se revelaram irreprodutíveis e foram corrigidos (ver "Vetores do corpus
corrigidos", abaixo); o valor medido no mesmo ponto elegível passou de 0,41 para **0,42**. Aplicando a
MESMA regra de arredondamento ao valor novo:

```
L2 = floor(0,42 / 0,05) × 0,05 = 0,40
```

— o piso ratificado pela ADR-005 é insensível a esta mudança. Não houve recalibração, não houve nova
ADR sobre o piso: `L2 = 0,40` continua sendo o piso vigente, agora com folga um pouco maior (ver "O
tamanho real da folga" abaixo).

**Por que isso não é "afrouxar a régua até passar".** O número não foi escolhido para caber — ele sai
mecanicamente da mesma fórmula que a spec já declarava, aplicada ao único ponto elegível, medido antes
de qualquer decisão de piso ser tomada. A causa do teto foi isolada com evidência direta, não palpite:
leave-one-out sobre as 150 descrições reais do corpus (cada uma usada como consulta contra as outras
149, via `<=>`) mede `meanPrecision@5 = 0,7173` — mesmo no cenário mais favorável concebível (a
"consulta" é o texto literal de uma descrição do corpus), só 3,6 dos 5 vizinhos mais próximos são da
mesma especialidade. Combinando essa medição com o teto estrutural das 5 consultas com localização
(1,40 no agregado, já registrado acima): `teto realista = (15 × 0,7173 + 1,40) / 20 ≈ 0,61` — abaixo
do 0,70 original mesmo no melhor caso possível. Ver "Hipóteses" abaixo para a cadeia completa de
diagnóstico (modelo → corpus → fórmula) que chegou a essa conclusão.

**O que `L2 = 0,40` passa a significar.** Deixou de ser uma ambição de qualidade — a métrica está
limitada pela composição do corpus (15 especialidades de vocabulário próximo entre si), não pelo
ranking, e nenhum modelo nem combinação de pesos neste espaço a move. `L2` passa a ser uma **guarda de
regressão**: existe para que uma mudança futura que derrube `meanPrecision@5` (um refactor que quebra
o cálculo de score, uma troca de modelo que piora a semântica, um peso mal calibrado) seja pega pelo
teste — não para certificar que a busca está "boa o bastante" em sentido absoluto.

**O tamanho real da folga (0,41 → 0,40), medido, não estimado.** `precision@5` por consulta é `k/5`
(`k ∈ {0..5}`) — o denominador é sempre 5 porque `n ≥ 5` nas 20 consultas do golden set (mesmo as 5
com localização têm de 8 a 18 candidatos dentro do raio, ver "Teto estrutural" acima) — logo cada
`precision@5` é múltiplo de 0,2, e `meanPrecision@5` (a média de 20 desses valores) só assume
múltiplos de `0,2 / 20 = 0,01`. A folga `0,41 − 0,40 = 0,01` era **exatamente uma unidade dessa
granularidade — a menor folga não-nula que a métrica consegue expressar**: uma única consulta
perdendo um relevante do top-5 ainda passava; duas perdas dessa magnitude (na mesma consulta ou em
consultas diferentes) reprovariam. (Descrição do estado em 2026-08-11, no momento da ADR-005.)

**Atualização (2026-08-11, MET-528, ADR-006).** Com o artefato do corpus corrigido, a folga medida
hoje é `0,42 − 0,40 = 0,02` — **duas** unidades dessa mesma granularidade, não uma: a correção dos 3
vetores não só subiu o número publicado, também dobrou a margem entre o medido e o piso. Continua uma
margem pequena (duas consultas perdendo um relevante do top-5, ou uma perdendo dois, ainda reprovam),
mas menos rente ao limite do que estava antes da correção.

**O que não foi feito, e por quê.** O dono decidiu, explicitamente, não mexer no corpus
(`db/seed/professionals.json`) nem nas 20 consultas (`eval/golden-set.json`) — a alavanca que a
evidência do leave-one-out sugeriria como a única capaz de mover `meanPrecision@5` de verdade — para
não reabrir a medição inteira nesta altura do case. Fica registrado como gatilho de revisão da
ADR-005, não como ação tomada aqui.

**`MinSemanticScore`: mantido em 0.** O problema medido não é ruído (candidato irrelevante entrando
por proximidade) — é precisão insuficiente mesmo sem localização nenhuma (as 15 consultas sem
localização têm o mesmo `precision@5` em todo o grid, porque sem localização a proximidade não entra
e o score é a semântica bruta — D4/ADR-003). Cortar pelo fator semântico não tem evidência de resolver
uma questão de quantos candidatos de OUTRAS especialidades ficam misturados no top-5 (ver hipóteses
abaixo); ligar o corte sem essa evidência fica registrado como gatilho de revisão da ADR-005, não
decisão tomada aqui.

**Sobreajuste, com o vencedor já conhecido — dito sem rodeio.** Desta vez a regra de escolha produziu
um vencedor (`0,9/5`), e as três mitigações declaradas antes de medir continuam valendo: o grid é
pequeno e foi fixado *a priori* (não se ampliou depois de ver o resultado desfavorável na primeira
leitura desta mesma T11); a regra de desempate favoreceria o modelo mais simples se houvesse mais de
um ponto elegível empatado — aqui nem chegou a ser necessária, porque só um ponto sobreviveu ao
primeiro filtro; a tabela **inteira** (18 pontos) é publicada acima, não só o vencedor. O piso `L2`,
por sua vez, não foi calibrado contra o mesmo conjunto que ele avalia no sentido de "escolher o número
que passa" — ele é uma função determinística (arredondamento) do valor medido no ponto já escolhido
pela regra independente dos pesos. Vinte consultas continuam medindo uma direção, não uma garantia; a
mudança de piso é sobre reconhecer o teto real dessa direção, não sobre fingir uma garantia maior.

### Vetores do corpus corrigidos e artefato regenerado (ADR-006, MET-528, 2026-08-11)

**A disciplina primeiro: este número SOBE (0,41 → 0,42), o movimento que este projeto trata como
suspeito por padrão.** Registrado aqui com a mesma severidade que se aplicou para BAIXAR o piso
(seção acima): a mudança não foi escolhida para melhorar o número — foi consequência mecânica de
corrigir um defeito de reprodutibilidade encontrado ao verificar outra issue (MET-527); o efeito na
régua foi medido com o eval oficial ANTES de qualquer decisão, e teria sido adotado mesmo se o número
tivesse piorado; e o piso ratificado pela ADR-005 não se move (seção acima).

**O achado.** Reembeddar as 150 descrições do corpus contra o mesmo `text-embedding-qwen3-embedding-0.6b`,
no mesmo LM Studio, e comparar vetor a vetor com o artefato então versionado devolveu 147 vetores
bit-idênticos e **3 diferentes** — não ruído de ponto flutuante, outro vetor:

| slug | especialidade | cosseno vs. artefato antigo |
|---|---|---:|
| `marcos-araujo-nit-008` | encanador | 0,8105 |
| `pedro-machado-bh-055` | chaveiro | 0,8754 |
| `vinicius-ferreira-rp-036` | diarista | 0,9026 |

**Cinco hipóteses eliminadas por medição, não por suposição** (`project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md`,
repo do harness, tem a medição completa de cada uma): atribuição trocada entre profissionais (o vetor
antigo de cada um dos 3 continua mais parecido com o PRÓPRIO documento do que com qualquer um dos
outros 149); truncamento de texto (o texto completo é o que mais se aproxima, não nenhum prefixo);
texto histórico diferente (`db/seed/professionals.json` tem uma única revisão no histórico do git);
documento contaminado (violaria D2 — nome/especialidade colados à descrição; oito composições
alternativas testadas, nenhuma se aproxima tanto quanto a descrição normalizada correta); e
sensibilidade a tamanho de lote (reembedação com lotes de 1, 32 e 150 devolveu os MESMOS 3 slugs com
os MESMOS vetores). A explicação restante mais plausível é uma falha transitória do provedor no
momento da geração original do artefato.

**A decisão do dono:** regenerar o artefato inteiro (os 150 `sourceHash` não mudaram — o texto do
corpus não mudou; só os 3 vetores mudaram — verificado explicitamente, não presumido) e travar a
reprodutibilidade por verificação executável, em vez de só regenerar e seguir. Duas camadas — uma que
roda sempre (coerência interna do artefato, sem provedor) e uma que roda só com provedor configurado
(reprodutibilidade contra o modelo real, skip explícito caso contrário) — documentadas em
`db/seed/README.md`, "Reprodutibilidade do artefato — verificação executável". Detalhe: o furo real
não eram os 3 vetores em si, era o pipeline não ter como perceber — o artefato antigo era
internamente consistente consigo mesmo (hash presente, contagem certa, `model`/`dimensions` corretos),
só não era reproduzível.

**O efeito na régua, medido antes de decidir** (mesma disciplina da T11 original — grid inteiro, sem
segunda rodada de ajuste): a tabela no topo desta seção já é essa medição. Resumo:

| | artefato anterior | artefato regenerado (MET-528) |
|---|---:|---:|
| `hitRate@3` (L1, piso 1,00) | 1,0000 | **1,0000** |
| `meanPrecision@5` (L2, piso 0,40) | 0,4100 | **0,4200** |
| ordem (L3) | 2/2 | **2/2** |
| L4 (consulta do DoD) | passa | **passa** — top-3 idêntico |

Nenhuma consulta passou a falhar L1 (inclusive `gs-02`, cujo alvo de `notes` é justamente
`marcos-araujo-nit-008` — o vetor de maior divergência); o ponto do grid escolhido pela regra de
escolha não muda (`w_s=0,9/τ=5` continua o único elegível a L1 e L3 nos dois artefatos — a calibração
da ADR-003 não é revisitada); e, como já mostrado acima, o piso da ADR-005 não se move. O único número
que muda é `meanPrecision@5`, subindo 0,01 em cada um dos 18 pontos do grid — o efeito de corrigir 2%
do corpus (3 de 150 vetores), medido, não estimado.

**Regenerar o artefato:** comando real, `dotnet run --project src/Prumo.SeedEmbeddings` (ver
`db/seed/README.md`, "Como regenerar") — o mesmo padrão de `src/Prumo.Eval` (que já resolvia este
problema para o artefato de CONSULTAS do golden set), agora também para o artefato do CORPUS. Antes da
MET-528, "regenerar o artefato do corpus" só existia em prosa; agora é um comando, com verificação de
reprodutibilidade (byte a byte, duas execuções) documentada ao lado dele.

### Chaves do artefato do corpus regeneradas — número da régua NÃO muda (MET-527, 2026-08-11)

`EmbeddingDocument.Hash` passou a normalizar a caixa na chave de identidade (racional completo em
`db/seed/README.md`, "Procedência do artefato de vetores" — "Revisão MET-527"): os 150 `sourceHash`
do artefato do corpus mudaram, os 150 `embedding` **não** (150/150 idênticos, comparados
numericamente contra o artefato anterior). O texto embeddado das ~20 consultas do golden set já era
todo minúsculo — nenhum `sourceHash` daquele artefato mudou, e o arquivo regenerado saiu byte a byte
idêntico ao anterior. Régua remedida depois da troca de chaves (mesmo comando, mesmo corpus, mesmo
banco): `hitRate@3 = 1,00`, `meanPrecision@5 = 0,42`, ordem 2/2, L4 passa — **idêntico** ao ponto
medido pela MET-528, porque nenhum vetor mudou.

### Hipóteses (ordem de diagnóstico pré-comprometida, aplicada à medição real)

A troca de modelo (ADR-004) resolveu o que a medição contra `bge-m3` apontava como suspeito
principal: `hitRate@3` subiu de 0,75 para 1,00 no ponto configurado, e todas as 20 consultas —
inclusive as 5 que falhavam antes (`gs-05`, `gs-07` revisada, `gs-09`, `gs-14`, `gs-18`) — agora têm
ao menos um profissional relevante no top-3. **Isto confirma a hipótese 1 da medição anterior**: o
modelo era, de fato, a causa da falha de L1.

Mas `meanPrecision@5` não se moveu na mesma proporção: **0,39–0,41 com `qwen3-embedding-0.6b`**
*(números da medição de 2026-08-11, antes da ADR-006; com o artefato corrigido o intervalo é
0,40–0,42 — a faixa de 0,02 entre os 18 pontos, que é o argumento aqui, não muda)*,
praticamente o MESMO intervalo medido com `bge-m3` (0,39–0,42), apesar de `hitRate@3` ter subido ~25
pontos percentuais. Inspeção direta (consulta SQL `<=>` contra o banco real, fora do teste, para não
alterar a régua) em cinco consultas sem localização confirma o padrão: o profissional relevante
aparece cedo (posição 1, no geral), mas o top-5 inteiro mistura especialidades — `gs-01` ("vazamento
no banheiro") traz 2 encanadores em 5 (as outras 3 posições: vidraceiro, eletricista, vidraceiro);
`gs-02` traz 2 encanadores em 5; `gs-09` (chaveiro) traz 2 chaveiros em 5; `gs-05` (pintor) traz 2
pintores em 5 (mais 2 diaristas — especialidade plausível mas fora de `expectedSpecialties`, que só
lista `pintor`); só `gs-13` (professor particular) chega a 4/5. Isso é consistente com
`meanPrecision@5 ≈ 0,40` medido no agregado: por volta de 2 de cada 5 resultados do top-5 pertencem à
especialidade esperada, independentemente de qual dos dois modelos gerou os vetores.

1. **O modelo de embeddings — já respondido, não é mais o suspeito principal.** A troca resolveu
   `hitRate@3` quase por completo (1,00 no ponto configurado, entre 0,85 e 1,00 em todo o grid) sem
   mover `meanPrecision@5`. Isso descarta o modelo como explicação para a falha de L2 remanescente —
   os dois modelos, com qualidades de ranking muito diferentes na métrica que mede "o topo está
   certo?", convergem para o MESMO teto em "o top-5 inteiro está certo?".
2. **A densidade e a distintividade do corpus por especialidade — suspeito principal agora.** O
   corpus tem 150 profissionais em 15 especialidades (10 cada). Para as 15 consultas sem localização,
   o filtro geográfico não restringe nada (D4) — os 140 profissionais de OUTRAS especialidades são
   todos candidatos, e o suficiente deles descreve serviços residenciais em linguagem próxima o
   bastante (conserto, resolução de problema doméstico, atendimento rápido) para ocupar 2 a 3 das 5
   posições do top-5 em quase toda consulta, mesmo quando o candidato mais relevante de todos vence a
   primeira posição. Isto é diferente do teto estrutural já documentado nesta seção (que é sobre
   `n` pequeno em consultas COM localização) — aqui `n` é o corpus inteiro (150) e ainda assim a
   precisão não sobe, porque o problema não é falta de candidatos relevantes, é excesso de candidatos
   IRRELEVANTES semanticamente próximos.

   **Medição direta da distintividade intrínseca do corpus (leave-one-out).** Para separar "o modelo
   erra" de "o corpus não é distintivo o bastante para qualquer modelo", cada uma das 150 descrições
   de `db/seed/professionals.json` foi usada como CONSULTA contra as outras 149 (a própria embedding
   real do profissional, já gravada em `professionals.embedding`, comparada por `<=>` contra as
   demais 149 linhas — SQL abaixo, fora do teste, para não alterar a régua):

   ```sql
   WITH loo AS (
     SELECT p.id AS query_id, p.specialty_id AS query_specialty,
            n.specialty_id AS neighbor_specialty,
            ROW_NUMBER() OVER (PARTITION BY p.id ORDER BY p.embedding <=> n.embedding) AS rn
     FROM professionals p JOIN professionals n ON n.id <> p.id
   ), top5 AS (
     SELECT query_id, query_specialty, neighbor_specialty FROM loo WHERE rn <= 5
   ), per_query AS (
     SELECT query_id, AVG((neighbor_specialty = query_specialty)::int::numeric) AS precision5
     FROM top5 GROUP BY query_id
   )
   SELECT AVG(precision5) FROM per_query;
   ```

   Resultado: **`meanPrecision@5` leave-one-out = 0,7173** (média sobre os 150 profissionais). Ou
   seja: mesmo na situação mais favorável concebível — a "consulta" é o texto literal de uma
   descrição do corpus, vocabulário e estilo idênticos ao candidato mais relevante possível —, em
   média só **3,6 dos 5** vizinhos mais próximos por `<=>` são da MESMA especialidade. Isto isola a
   distintividade do corpus do efeito de "consulta em linguagem de cliente, sem vocabulário": mesmo
   sem esse efeito, o corpus não separa as 15 especialidades o bastante para 5/5.

   **Projeção de um teto realista para `meanPrecision@5` do golden set completo**, combinando esta
   medição com o teto estrutural já registrado acima (a tabela de 5 consultas com localização, cujo
   teto médio por consulta é `1,40 / 5 = 0,28`, já contas na régua de 200 km/candidatos elegíveis):
   assumindo que as 15 consultas sem localização, no melhor caso possível, alcançassem o mesmo
   `0,7173` que a distintividade intrínseca do corpus permite —

   ```
   teto realista = (15 × 0,7173 + 1,40) / 20 = 12,16 / 20 ≈ 0,61
   ```

   **0,61 < 0,70 — o piso L2 é inalcançável com este corpus, mesmo num cenário irrealisticamente
   favorável** (consulta = texto literal do corpus; nenhuma consulta real de cliente chega a esse
   patamar de proximidade lexical/semântica com a descrição-alvo, já que o golden set exige ponte
   conceitual, não paráfrase). Isto é evidência direta — não apenas inferência por eliminação — de
   que o gargalo é a distintividade do corpus entre especialidades, não o modelo escolhido nem a
   fórmula do ranking.
3. **A fórmula — descartada de novo, com evidência mais forte que na medição anterior.** O ponto mais
   favorável do grid para `meanPrecision@5` (0,41, no único ponto elegível) mal se move em relação ao
   pior (0,39) — uma faixa de 0,02 entre os 18 pontos, contra um piso que exige subir 0,29. *(Valores
   de 2026-08-11, antes da ADR-006; hoje são 0,42 e 0,40, e a faixa de 0,02 é a mesma.)* Nenhuma
   combinação de peso e `τ` resolve um problema que está na composição do corpus, não na combinação
   dos dois fatores.

**Conclusão da medição (T11 reexecutada, MET-524):** a causa que a primeira medição apontava (o
modelo) foi resolvida — `hitRate@3` subiu de 0,75 para 1,00. O gargalo remanescente é
`meanPrecision@5`, e a hipótese com evidência mais forte não é só inferência por eliminação — é
**medida diretamente**: a distintividade intrínseca do corpus (leave-one-out,
`meanPrecision@5 = 0,7173`) projeta um teto realista de **≈ 0,61** para o golden set completo, abaixo
do piso ORIGINAL de 0,70 mesmo no cenário mais favorável possível. O corpus (15 especialidades de 10
profissionais cada, com vocabulário de "serviço doméstico" suficientemente próximo entre elas) não é
distintivo o bastante para o piso original, com nenhum modelo de embeddings nem nenhuma combinação de
pesos.

**Decisão do dono (2026-08-11, registrada em ADR-005, fora desta medição):** em vez de ampliar o
corpus, tornar as descrições mais distintivas ou revisar o golden set — qualquer uma reabriria a
medição inteira —, o dono ratificou um piso `L2` menor (0,40, derivado mecanicamente do valor medido
no único ponto elegível pela mesma regra de arredondamento que a spec já usa) e os pesos que a regra
de escolha já apontava (`w_s=0,9`, `τ=5`). Com isso, a régua do M1 fecha: `L1`, `L2` (revisado), `L3` e
`L4` todos satisfeitos no ponto agora configurado em `appsettings.json`. Ver a seção "Piso `L2` baixado
de 0,70 para 0,40" acima para o racional completo, e
`project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md` (repo do harness) para a ADR.

## Congelamento

O golden set, as métricas e o limiar ratificado ficam **congelados** quando a MET-479 fecha (T12). A
partir daí:

- mudar consulta, resultado esperado, métrica ou limiar ⇒ **ADR + decisão do dono**
  (`project/adr/README.md`);
- mudar as **descrições do corpus** (`db/seed/professionals.json`, MET-478) passa a mexer na régua e
  cai na mesma regra;
- mudar **fórmula ou pesos do ranking** ⇒ nova ADR que supersede a ADR-003 — inclusive "só ajustar um
  peso".

Antes do congelamento (durante a T11), uma consulta só pode ser ajustada se estiver **errada ou
ambígua** no corpus real, e o ajuste vai registrado no relatório da task que o fez. Consulta não se
remove nem se reescreve só porque falhou — isso é ajustar a régua para o código passar, e é
exatamente o que este projeto se recusa a fazer (`PROJECT-MISSION.md` § Fronteiras invioláveis #3).

## `eval/embeddings/text-embedding-qwen3-embedding-0.6b.json` (T10 — vetores das consultas do golden set)

Gerado com o **mesmo modelo** do artefato do corpus (`db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json`,
procedência completa documentada em `db/seed/README.md` — modelo, quantização, ausência de prefixo de
instrução, tudo vale igual aqui e não é repetido nesta seção). **Modelo trocado na MET-524**
(`project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md`, repo do harness) — era
`text-embedding-bge-m3.json` (gate humano concluído em 2026-08-10, T10 original); regenerado em
2026-08-11 com `qwen3-embedding-0.6b`, mesmo formato, mesma dimensão (1024), nenhuma migration.

- **Formato:** o mesmo do corpus (`model`, `dimensions`, `hashAlgorithm`, `vectors[]`), com dois
  campos adicionais por entrada — `id` (o `id` da consulta em `golden-set.json`) e `text` (o texto
  da consulta; **não é segredo**, já está versionado em `golden-set.json` — é ele que alimenta
  `exampleQueries` de `GET /api/search/options`, D8 da spec MET-479).
- **`model` idêntico ao artefato do corpus** — `openai-compatible:text-embedding-qwen3-embedding-0.6b@1024`.
  `PrecomputedEmbeddingStore.Load` falha no boot se os dois artefatos declararem `model` diferentes
  (BSC-20). **Testado, não é conferência visual**:
  `tests/Prumo.Api.Tests/Eval/GoldenSetEmbeddingsArtifactTests.cs` carrega os DOIS artefatos REAIS
  deste repo (não fixtures) pelo mesmo `PrecomputedEmbeddingStore.Load` que a busca usa e afirma a
  igualdade dos dois `ModelId` por igualdade explícita (`CorpusAndQueryEmbeddingArtifacts_DeclareTheIdenticalModel`),
  além de carregá-los JUNTOS, no mesmo caminho de boot de `Embeddings:PrecomputedPaths`
  (`CorpusAndQueryEmbeddingArtifacts_LoadTogether_WithoutThrowingAndWithoutDroppingEntries`). O mesmo
  arquivo também afirma que **toda** consulta real de `golden-set.json` tem `sourceHash` presente no
  artefato real (`EveryGoldenSetQueryTextHash_ExistsInTheQueryEmbeddingsArtifact`) — a guarda irmã de
  `SeedCorpusTests.EveryProfessionalServiceDescriptionHash_ExistsInThePrecomputedEmbeddingsArtifact`
  (T9): sem ela, editar o `text` de uma consulta sem regenerar o artefato deixaria a régua e o
  artefato divergirem em silêncio (a consulta editada cairia em 422, enquanto `exampleQueries`
  continuaria servindo o texto ANTIGO que sobrevive no artefato).
- **`sourceHash` calculado pela MESMA função** da ingestão e da busca em runtime —
  `EmbeddingDocument.Hash(EmbeddingDocument.For(text))`, reusada sem cópia — é o que permite ao
  `SearchQueryEmbedder` (MET-479/T5) achar o vetor certo ao receber a consulta digitada pelo usuário
  (design.md, MET-479, §5.2, passo 1). **Sem assimetria de normalização entre ingestão e busca**: o
  endpoint só faz `Trim()` antes de repassar a consulta a `EmbedAsync` (idempotente com o que
  `EmbeddingDocument.For` já faz por conta própria — trim + colapso de espaços + NFC), então
  digitar/clicar o texto exatamente como está em `golden-set.json` sempre produz, dos dois lados, o
  mesmo hash.
- **Ordem do arquivo = ordem de `golden-set.json`** (`gs-01` a `gs-20`) — é a ordem que
  `PrecomputedEmbeddingStore.Entries` preserva e que `GET /api/search/options` usa para montar
  `exampleQueries` (até `Search:ExampleQueryLimit`, default 8): as 8 primeiras consultas de
  demonstração da tela são `gs-01`…`gs-08`, começando pela frase do case.
- **Determinístico, sem timestamp, EOL LF** — duas gerações contra o mesmo endpoint (LM Studio
  local, `qwen3-embedding-0.6b`/`Q8_0`), comparadas byte a byte: **arquivo idêntico** entre as duas
  execuções (`diff` sem saída).
- **Verificado de fato** (não presumido): com `Embeddings__Provider=precomputed` e
  `Embeddings__PrecomputedPaths` apontando para os dois artefatos (corpus + consultas — ver
  `.env.example`), `GET /api/search?q=vazamento+no+banheiro` responde **200**, `mode: "precomputed"`,
  sem nenhuma chave configurada e sem o LM Studio no ar — a demonstração pública da tela.

### Como regenerar

Comando real e versionado (achado do Reviewer da T10: a primeira passada só descrevia isso em
prosa) — `src/Prumo.Eval` (`dotnet run --project src/Prumo.Eval`), o mesmo padrão de
`src/Prumo.Seed` (`Host.CreateApplicationBuilder`, `EmbeddingProviderRegistration.AddEmbeddingProvider`,
nenhum `PackageReference` novo), reusando `EmbeddingDocument.For`/`.Hash` e o `IEmbeddingProvider`
configurado — nunca uma reimplementação paralela de normalização, hash ou chamada HTTP:

```bash
# A partir da raiz do repo. Suba o LM Studio servindo text-embedding-qwen3-embedding-0.6b
# (quantização Q8_0) em http://localhost:1234/v1 primeiro (mesmo servidor que gerou o artefato do
# corpus).
export Embeddings__Provider=openai-compatible
export Embeddings__BaseUrl=http://localhost:1234/v1
export Embeddings__Model=text-embedding-qwen3-embedding-0.6b
export Eval__OutputPath=eval/embeddings/text-embedding-qwen3-embedding-0.6b.json
dotnet run --project src/Prumo.Eval
```

Lê `eval/golden-set.json` (`Eval:GoldenSetPath`, default), calcula `EmbeddingDocument.For`/`.Hash`
para cada `text`, chama o `IEmbeddingProvider` configurado e escreve
`eval/embeddings/text-embedding-qwen3-embedding-0.6b.json` (`Eval:OutputPath`; é também o default do
código desde a MET-524 — `EvalProgram.DefaultOutputPath`) no formato acima, na ordem de
`golden-set.json`, sem timestamp, EOL LF, sem BOM. **Verificado**: a saída deste comando reproduz o
artefato versionado deste repo byte a byte (mesma checagem de determinismo bit-a-bit da T9 — ver
acima). Trocar de modelo/quantização é o mesmo comando com `Embeddings__Model` diferente e
`Eval__OutputPath=eval/embeddings/<modelo-novo>.json` — nome de arquivo novo, mesma convenção do
corpus (`PrecomputedEmbeddingStore.Load` detecta `model` divergente entre arquivos e derruba o boot).

## Estado atual: régua fechada (ADR-005, 2026-08-11; vetores do corpus corrigidos na MET-528/ADR-006)

**T10 regenerada com o modelo novo** (2026-08-11, MET-524): `db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json`
(corpus, T9) e `eval/embeddings/text-embedding-qwen3-embedding-0.6b.json` (consultas do golden set,
T10) existem, ambos com `model: "openai-compatible:text-embedding-qwen3-embedding-0.6b@1024"`; os
artefatos do `bge-m3` foram removidos. **T11 rodou pela segunda vez** (a primeira, contra `bge-m3`,
está preservada apenas na branch `met-479-t11`, não mesclada) — `tests/Prumo.Api.Tests/Integration/GoldenSetEvalTests.cs`
mede as 20 consultas contra Postgres + pgvector real, com vetores REAIS, pela mesma camada de busca
da API. Essa segunda medição fechou L1 e L4 (`hitRate@3 = 1,00`), tornou L3 alcançável em um ponto do
grid, mas não fechou L2 contra o piso ORIGINAL da spec (`meanPrecision@5 ≥ 0,70` — o melhor ponto
elegível mediu 0,41 nessa medição). A tabela completa medida, a comparação só-semântica vs. híbrido,
as hipóteses (ordem de diagnóstico pré-comprometida) e a evidência de teto estrutural do corpus estão
na seção "Grid de calibração e limiares" acima.

**Fora desta medição, o dono decidiu** (`project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md`,
repo do harness, 2026-08-11): ratificar `L2 = 0,40` — derivado mecanicamente do valor medido no único
ponto elegível pela mesma regra de arredondamento que a spec já declarava — e adotar os pesos que a
regra de escolha já apontava (`w_s = 0,9`, `w_p = 0,1`, `τ = 5`). `appsettings.json:Ranking` foi
atualizado com esses valores; `GoldenSetEvalTests.L2Threshold` foi atualizado para 0,40, com comentário
citando a ADR-005. Corpus, golden set, artefatos, L1, L3 e o grid de 18 pontos permaneceram exatamente
como estavam — nada disso foi tocado por essa decisão. *(Os artefatos só viriam a mudar depois, pela
ADR-006, e por outro motivo: 3 vetores irreproduzíveis.)*

**Vetores do corpus corrigidos (2026-08-11, MET-528, `project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md`
no repo do harness).** 3 dos 150 vetores do artefato do corpus se revelaram irreprodutíveis pelo
modelo declarado (achado ao verificar outra issue, MET-527) — cinco hipóteses eliminadas por medição,
regenerados por inteiro (150 `sourceHash` inalterados, verificado), e a reprodutibilidade travada por
duas camadas de verificação executável (`db/seed/README.md`, "Reprodutibilidade do artefato"). A régua
remedida com o eval oficial: `hitRate@3 = 1,00`, `meanPrecision@5 = 0,42` (era 0,41), ordem 2/2, L4
passa — ponto adotado e piso `L2 = 0,40` inalterados (ver "Vetores do corpus corrigidos" acima para o
racional completo e a tabela antes/depois). **A régua do M1 fecha**: L1, L2 (revisado), L3 e L4
satisfeitos no ponto configurado, com o artefato do corpus atual.
