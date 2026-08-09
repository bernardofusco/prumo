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

## Pendência conhecida

**T10/T11 dependem de decisão humana ainda em aberto** (MET-478/P1 — provedor de embeddings real).
Sem `db/seed/embeddings/<modelo>.json` e `eval/embeddings/<modelo>.json`, não existe vetor semântico
real neste repo, e o eval do golden set contra Postgres não pode rodar de verdade. Enquanto isso, o
que este diretório garante é a **conformidade estrutural** da régua (T3) — a régua está pronta para
medir assim que os vetores existirem.
