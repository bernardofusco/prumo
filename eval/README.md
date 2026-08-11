# `eval/` — a régua do M1 (busca com ranking híbrido)

Este diretório é o instrumento de medição da MET-479 (`specs/features/met-479-busca-ranking-hibrido/`,
seção **"Medição do Case"**, normativa). Ele existe para uma frase só: *"vazamento no banheiro"
encontra encanador sem a palavra "encanador" aparecer em lugar nenhum* — e esse README documenta como
isso é medido, o que a medição prova e, principalmente, o que ela **não** prova.

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

Dito isso, como o cálculo do baseline lexical é determinístico sobre texto já congelado nesta revisão
(não depende de nenhum provedor de embeddings), o número já pode ser medido hoje, sem vetor nenhum:
contra o golden set desta revisão, o baseline lexical erra 9 das 20 consultas no top-3
(`hitRate@3 = 0,55`) — abaixo do piso L1 = 1,00 que a busca semântica precisa cumprir, e é exatamente a
folga que torna o conjunto discriminante. Este número é informativo (prova a propriedade que a
asserção do teste exige); o valor oficial, ao lado de semântica e híbrido na mesma tabela, é o que a
T11 publica.

## Grid de calibração e limiares — ainda não medidos

A tabela completa do grid (18 pontos: `semanticWeight × distanceDecayKm`), a comparação
só-semântica vs. híbrido e os pesos efetivamente escolhidos **ficam em branco até a T11** rodar o eval
de verdade contra Postgres + pgvector com vetores reais. Os limiares (`hit@3 = 1,00`,
`precision@5 ≥ 0,70`, ordem 100% satisfeita) estão fixados na spec (MET-479, "Medição do Case →
Limiares") e não são decisão desta task — nenhum agente decide piso ou peso; quem mede é a T11, quem
ratifica é o dono (spec, P2).

<!-- T11 preenche a tabela abaixo; T12 confere que os números aqui batem com a saída do teste. -->

| `semanticWeight` | `distanceDecayKm` | `hitRate@3` | `meanPrecision@5` | ordem 100%? |
|---|---|---|---|---|
| *(pendente — T11)* | | | | |

**Pesos escolhidos:** *(pendente — T11, com a regra de escolha da spec aplicada e justificada aqui)*

**Só-semântica vs. híbrido:** *(pendente — T11; o ponto `semanticWeight = 1,0` do grid é a própria
linha de base)*

**Sobreajuste, dito sem rodeio:** calibrar até 18 pontos contra as mesmas 20 consultas que os avaliam
é sobreajuste — o case não finge o contrário. Três mitigações, nenhuma delas prova generalização:
o grid é pequeno e declarado *a priori* (não se amplia depois de ver o resultado); o desempate
favorece o modelo mais simples (maior `semanticWeight`, menor dependência do fator geográfico); a
tabela **inteira** é publicada, não só o vencedor. Vinte consultas medem uma direção, não uma
garantia.

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

## `eval/embeddings/text-embedding-bge-m3.json` (T10 — vetores das consultas do golden set)

Gerado uma única vez (gate humano concluído em 2026-08-10), com o **mesmo modelo** do artefato do
corpus (`db/seed/embeddings/text-embedding-bge-m3.json`, procedência completa documentada em
`db/seed/README.md` — modelo, quantização, ausência de prefixo de instrução, tudo vale igual aqui e
não é repetido nesta seção).

- **Formato:** o mesmo do corpus (`model`, `dimensions`, `hashAlgorithm`, `vectors[]`), com dois
  campos adicionais por entrada — `id` (o `id` da consulta em `golden-set.json`) e `text` (o texto
  da consulta; **não é segredo**, já está versionado em `golden-set.json` — é ele que alimenta
  `exampleQueries` de `GET /api/search/options`, D8 da spec MET-479).
- **`model` idêntico ao artefato do corpus** — `openai-compatible:text-embedding-bge-m3@1024`.
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
  local, `bge-m3`/`Q8_0`), comparadas componente a componente: **0 de 20.480 valores divergentes**
  (20 vetores × 1024 dimensões, float32).
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
# A partir da raiz do repo. Suba o LM Studio servindo text-embedding-bge-m3 (quantização Q8_0) em
# http://localhost:1234/v1 primeiro (mesmo servidor que gerou o artefato do corpus).
export Embeddings__Provider=openai-compatible
export Embeddings__BaseUrl=http://localhost:1234/v1
export Embeddings__Model=text-embedding-bge-m3
dotnet run --project src/Prumo.Eval
```

Lê `eval/golden-set.json` (`Eval:GoldenSetPath`, default), calcula `EmbeddingDocument.For`/`.Hash`
para cada `text`, chama o `IEmbeddingProvider` configurado e escreve
`eval/embeddings/text-embedding-bge-m3.json` (`Eval:OutputPath`, default) no formato acima, na
ordem de `golden-set.json`, sem timestamp, EOL LF, sem BOM. **Verificado**: a saída deste comando
reproduz o artefato versionado deste repo byte a byte (mesma checagem de determinismo bit-a-bit da
T9 — ver acima). Trocar de modelo/quantização é o mesmo comando com `Embeddings__Model` diferente e
`export Eval__OutputPath=eval/embeddings/<modelo-novo>.json` — nome de arquivo novo, mesma convenção
do corpus (`PrecomputedEmbeddingStore.Load` detecta `model` divergente entre arquivos e derruba o
boot).

## Pendência conhecida

**T10 concluída** (2026-08-10): `db/seed/embeddings/text-embedding-bge-m3.json` (corpus, T9) e
`eval/embeddings/text-embedding-bge-m3.json` (consultas do golden set, T10) existem, ambos com
`model: "openai-compatible:text-embedding-bge-m3@1024"`. **T11 (o eval, o grid de 18 pontos e a
calibração dos pesos) ainda não rodou** — a tabela da seção "Grid de calibração e limiares" acima,
os limiares L1–L4 medidos de verdade e a comparação só-semântica vs. híbrido continuam pendentes
dessa task, não desta. Enquanto T11 não roda, o que este diretório garante é a **conformidade
estrutural** da régua (T3) — a régua está pronta para medir.
