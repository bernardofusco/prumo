# `db/seed/` — corpus de demonstração (MET-478)

Todos os dados aqui são **fictícios**, criados para o case. Nenhum profissional, cliente, telefone,
e-mail, documento ou endereço real. Coordenadas são de **centros de cidades reais** de MG/RJ/SP (dado
geográfico público), com um pequeno deslocamento já calculado e gravado no arquivo — nada é sorteado
em tempo de execução.

## Arquivos

| Arquivo | O que é | Gerado? |
|---|---|---|
| `specialties.json` | As especialidades do corpus: `slug`, `name` (exibição) e `corpusSynonyms` — vocabulário óbvio da especialidade, usado **só** pelo teste de conformidade (`SeedCorpusTests`) para medir ING-08. `corpusSynonyms` **não é persistido** no banco. | Não — escrito à mão, versionado como dado (D7, `specs/features/met-478-modelagem-e-ingestao/design.md` §5.1). |
| `professionals.json` | 150 profissionais sintéticos: nome fictício, especialidade, descrição de serviço, cidade/estado, coordenadas e raio de atendimento. | Não — escrito à mão, versionado como dado (D7, design §5.2). |
| `embeddings/text-embedding-qwen3-embedding-0.6b.json` | Vetores pré-computados das descrições acima, indexados pelo hash do documento (`EmbeddingDocument.Hash`). | **Sim** — artefato gerado pela task T9 (gate humano), com o modelo decidido em `project/adr/ADR-002-provedor-de-embeddings.md` (harness). Gerado em 2026-08-10 (`bge-m3`); regenerado em 2026-08-11 com `qwen3-embedding-0.6b` (`project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md`, harness); **3 dos 150 vetores regenerados de novo em 2026-08-11 (MET-528)** por não serem reprodutíveis pelo modelo declarado — `project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md` (harness). Gerado pelo comando real `dotnet run --project src/Prumo.SeedEmbeddings` (ver "Como regenerar" abaixo). |

## Procedência do artefato de vetores (T9; modelo trocado na MET-524; 3 vetores corrigidos na MET-528)

> **Revisão MET-528 (2026-08-11, ADR-006).** Ao verificar outra issue (MET-527), reembeddar as 150
> descrições e comparar vetor a vetor com o artefato então versionado revelou que **3 dos 150 vetores
> não eram reprodutíveis** pelo modelo declarado — `marcos-araujo-nit-008` (cosseno 0,8105 contra o
> artefato antigo), `pedro-machado-bh-055` (0,8754), `vinicius-ferreira-rp-036` (0,9026); os outros 147
> reproduziram bit a bit. Cinco hipóteses foram eliminadas por medição, não por suposição — atribuição
> trocada entre profissionais, truncamento de texto, texto histórico (o corpus só tem uma revisão no
> histórico do git), documento contaminado (nome/especialidade colados à descrição, violando D2) e
> sensibilidade a tamanho de lote (testado com lotes de 1, 32 e 150 — mesmos 3 slugs, mesmos vetores em
> todos) — restando como explicação mais plausível uma falha transitória do provedor no momento da
> geração original. O artefato foi **regenerado por inteiro** (os 150 `sourceHash` não mudaram — o
> texto do corpus não mudou; só os 3 vetores mudaram) e a reprodutibilidade foi travada por duas
> camadas de verificação executável (ver "Reprodutibilidade do artefato — verificação executável"
> abaixo). Efeito medido na régua do M1: `meanPrecision@5` sobe de 0,41 para 0,42 no ponto adotado; o
> piso `L2 = 0,40` (ADR-005) **não muda** — ver `eval/README.md`.

> **Revisão MET-524 (2026-08-11):** o artefato do corpus (e o de consultas do golden set, `eval/`)
> foi regenerado com `qwen3-embedding-0.6b` — o modelo `bge-m3` original reprovou a régua na T11
> (`eval/README.md`, "Grid de calibração e limiares"). Racional completo, medições dos dois modelos
> e disciplina de medição única em `project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md`
> (harness). A arquitetura não muda (modelo local via LM Studio, artefato pré-computado, custo zero —
> ADR-002) nem a dimensão (1024, MET-521) — **nenhuma migration**. Os artefatos do `bge-m3` foram
> removidos do repo (dois modelos convivendo em `Embeddings:PrecomputedPaths` derrubam o boot por
> `model` divergente — testado, `PrecomputedEmbeddingStoreEntriesTests`).

- **Modelo:** `qwen3-embedding-0.6b` (Qwen), servido localmente pelo LM Studio como
  `text-embedding-qwen3-embedding-0.6b` (publisher `Qwen`, arquitetura `qwen3`, formato `gguf`) em
  `http://localhost:1234/v1` (endpoint OpenAI-compatible, decisão B da ADR-002).
- **Quantização:** `Q8_0` — confirmada via `GET /api/v0/models` do próprio LM Studio (API estendida
  que expõe o campo `quantization`), não presumida.
- **Dimensão:** 1024 — saída **nativa** do modelo (LM Studio ignora o parâmetro `dimensions` do
  request, mesmo comportamento já observado com `bge-m3`). Casa com `embedding vector(1024)`
  (`db/migrations/0004_professional_embedding_dimension_1024.sql`) — **nenhuma migration** nova foi
  necessária para esta troca de modelo.
- **`model` gravado no artefato e por linha em `professionals.embedding_model`:**
  `openai-compatible:text-embedding-qwen3-embedding-0.6b@1024` (formato de
  `IEmbeddingProvider.ModelId`, prefixo `openai-compatible` porque o mesmo cliente serve OpenAI e LM
  Studio — ver `OpenAiCompatibleEmbeddingProvider`).
- **Sem prefixo de instrução aplicado.** Nenhum prefixo de instrução (`query:`/`passage:`, convenção
  da família E5) é aplicado na ingestão nem na busca — mesma decisão já vigente com `bge-m3`. A
  medição da T11 (`eval/README.md`) mostra `hitRate@3 = 1,00` no ponto configurado sem prefixo, o que
  não sustentaria essa taxa se o modelo exigisse um prefixo simétrico não aplicado. **Se um modelo
  futuro exigir prefixo, isso precisa ser aplicado nos dois lados — ingestão e busca (MET-479) — de
  forma simétrica, e documentado aqui.**
- **Nenhuma credencial usada.** LM Studio local não exige `Embeddings__ApiKey`.
- **Determinismo confirmado bit-a-bit.** Verificado de duas formas: (1) o artefato de CONSULTAS do
  golden set (`eval/embeddings/text-embedding-qwen3-embedding-0.6b.json`, mesmo endpoint/modelo) foi
  gerado duas vezes e os dois arquivos são **idênticos byte a byte** (`diff` sem saída); (2) uma
  chamada isolada a `POST /v1/embeddings` com o mesmo texto, repetida, devolveu a mesma resposta HTTP
  byte a byte. Regenerar hoje, nesta máquina, reproduz o artefato exatamente — não é só a
  normalização/hash que é determinística (isso já era garantido por construção,
  `EmbeddingDocument.Hash`); a inferência do próprio modelo também é.

## Como regenerar

- `specialties.json` e `professionals.json` **não têm gerador automático** — são dado versionado
  (D7). Editar é abrir o JSON e mudar à mão, respeitando o formato de
  `design.md` §5 e as constraints de `db/migrations/0002_specialties_and_professionals.sql`
  (formato de slug, `state` em duas maiúsculas, faixas de latitude/longitude/raio, descrição com
  pelo menos 40 caracteres).
- `embeddings/text-embedding-qwen3-embedding-0.6b.json`: comando real e versionado, desde a MET-528
  (achado análogo ao que já motivou `src/Prumo.Eval` para o artefato de consultas do golden set —
  antes disso, só existia a prosa "rode a ingestão contra um banco descartável e exporte as colunas",
  sem nenhum comando de verdade). Suba o LM Studio servindo `text-embedding-qwen3-embedding-0.6b`
  (quantização `Q8_0`) em `http://localhost:1234/v1` e, a partir da raiz do repo:

  ```sh
  export Embeddings__Provider=openai-compatible
  export Embeddings__BaseUrl=http://localhost:1234/v1
  export Embeddings__Model=text-embedding-qwen3-embedding-0.6b
  dotnet run --project src/Prumo.SeedEmbeddings
  ```

  Lê `db/seed/specialties.json`/`db/seed/professionals.json` (`SeedCorpusReader.Load` — a MESMA
  validação que a ingestão usa, `Seed:SpecialtiesPath`/`Seed:ProfessionalsPath` para apontar para
  outro corpus), calcula `EmbeddingDocument.For`/`.Hash` por profissional, chama o `IEmbeddingProvider`
  configurado em lotes de 32 (`SeedEmbeddings:EmbeddingBatchSize`, mesmo default de
  `SeedRunnerOptions.DefaultEmbeddingBatchSize` — um único request com os 150 documentos estoura o
  timeout fixo de 30s de `OpenAiCompatibleEmbeddingProvider`) e escreve
  `db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json` (`SeedEmbeddings:OutputPath`), na
  MESMA forma do artefato (`slug`/`sourceHash`/`embedding`), **ordenado por `slug`**, sem timestamp,
  EOL LF, sem BOM — **nunca abre conexão com Postgres**: "regenerar o artefato" e "popular o banco"
  são operações independentes (o Seed, com `Embeddings:Provider=precomputed`, só CONSOME um artefato
  já pronto). Trocar de modelo/quantização é o mesmo comando com `Embeddings__Model` diferente e
  `SeedEmbeddings__OutputPath=db/seed/embeddings/<modelo-novo>.json` — nome de arquivo novo, mesma
  convenção do artefato de consultas do golden set (`PrecomputedEmbeddingStore.Load` detecta `model`
  divergente entre arquivos e derruba o boot).

  **Verificado (MET-528):** rodar o comando acima duas vezes seguidas produz dois arquivos
  **idênticos byte a byte** (`diff` sem saída) e os 150 `sourceHash` batem 150/150 contra
  `db/seed/professionals.json` — reprodutibilidade confirmada, não presumida. Ver "Reprodutibilidade
  do artefato — verificação executável", abaixo, para a régua automatizada equivalente.

## ⚠️ Aviso — o corpus é a régua do M1

O golden set de busca da MET-479 é medido **contra as descrições deste `professionals.json`**. Em
particular, o requisito ING-08 (≥ 20% das descrições sem o nome da especialidade nem os
`corpusSynonyms` declarados) é o que torna a busca semântica distinguível de uma busca por palavra-chave.

**Depois que o golden set da MET-479 estiver congelado, mudar qualquer descrição deste arquivo muda
a régua** — e passa a exigir ADR (`project/adr/README.md`: mudança no golden set é decisão do dono),
não uma edição silenciosa. Enquanto o golden set não existe, este corpus é livre para ajuste sem esse
custo (`spec.md`, seção "Medição do Case").

## Reprodutibilidade do artefato — verificação executável (MET-528, ADR-006)

O defeito que motivou a MET-528 (3 dos 150 vetores não reproduzíveis pelo modelo declarado — ver
"Procedência do artefato de vetores" acima) não foi pego por nenhum gate existente até então: o
artefato era internamente consistente consigo mesmo (formato válido, hash presente, contagem certa),
só não era **reproduzível** pelo provedor declarado — e nada verificava isso. A resposta são **duas
camadas**, deliberadamente diferentes uma da outra, porque cobrem defeitos diferentes:

**(a) Coerência interna do artefato — roda sempre, inclusive sem provedor nenhum configurado (CI
padrão).** `tests/Prumo.Api.Tests/SeedCorpusTests.cs` (seção "guarda do artefato de embeddings") lê
`db/seed/professionals.json` e o artefato real, sem banco e sem rede, e falha se:

- algum `sourceHash` calculado por `EmbeddingDocument.Hash(EmbeddingDocument.For(serviceDescription))`
  de um profissional real não existir no artefato (descrição editada sem regenerar);
- alguma entrada do artefato for **órfã** — um `slug` que não corresponde a nenhum profissional real
  do corpus (artefato colado de outra geração/corpus, ou profissional removido/renomeado);
- a contagem de vetores do artefato não bater com a contagem de profissionais do corpus;
- `model`/`dimensions` declarados no artefato não baterem com o esperado.

Isto pega **deriva entre corpus e artefato** — a falha mais provável no dia a dia (alguém edita uma
`serviceDescription` e esquece de regenerar) — mas **não pega** o defeito real da MET-528: um artefato
pode estar 100% coerente consigo mesmo e ainda assim não ser reproduzível pelo provedor declarado (foi
exatamente o caso aqui — os 3 vetores irreprodutíveis tinham hash, contagem, `model` e `dimensions`
corretos).

**(b) Reprodutibilidade contra o provedor real — roda só quando um provedor está configurado; skip
explícito caso contrário.** `tests/Prumo.Api.Tests/EmbeddingsArtifactReproducibilityTests.cs` reembeda
o corpus real contra `Embeddings:Provider=openai-compatible` e compara vetor a vetor (similaridade por
cosseno ≥ 0,999) com o artefato versionado — é a verificação que **teria pegado** o defeito da MET-528
(os 3 vetores então divergentes mediam cosseno entre 0,81 e 0,90, muito abaixo do limiar). Sem chamada
de rede automática por padrão (custo/latência de provedor externo é gate humano, não CI): o teste usa
um `[Fact]` condicional (`EmbeddingProviderFactAttribute`) que aparece como `Skipped` — nunca falha, nem
passa vacuamente — quando as três variáveis abaixo não estão exportadas, com a mensagem de skip
repetindo exatamente este comando:

```sh
export Embeddings__Provider=openai-compatible
export Embeddings__BaseUrl=http://localhost:1234/v1
export Embeddings__Model=text-embedding-qwen3-embedding-0.6b
dotnet test --filter "FullyQualifiedName~EmbeddingsArtifactReproducibilityTests"
```

Roda no gate `full` (`Category!=Integration`) do harness — não precisa de Docker, só do provedor no
ar; sem as três variáveis, o gate `full` continua verde com o teste marcado `Skipped`, exatamente como
qualquer execução sem LM Studio configurado (a maioria, inclusive CI).

## Conformidade verificada por teste

`tests/Prumo.Api.Tests/SeedCorpusTests.cs` lê os dois arquivos acima (sem banco, sem rede) e falha se
qualquer um destes deixar de valer:

- 100–200 profissionais (alvo 150); ≥ 12 cidades reais de MG/RJ/SP; ≥ 10 especialidades; nenhuma
  especialidade com um único profissional.
- Todos os `slug` (especialidade e profissional) únicos e no formato `^[a-z0-9]+(-[a-z0-9]+)*$`
  exigido pela migration `0002`.
- Todas as `serviceDescription` distintas entre si e com pelo menos 40 caracteres (depois de `trim`).
- Todo `specialtySlug` de `professionals.json` existe em `specialties.json`.
- `state` em duas letras maiúsculas; latitude/longitude/raio dentro das faixas da migration `0002`.
- ≥ 20% das descrições sem o nome da especialidade nem nenhum `corpusSynonyms` declarado (ING-08).
- Nenhum campo de contato: o teste varre **nomes de propriedade JSON** (recusa `telefone`, `email`,
  `cpf`, `cnpj`, `endereco` etc. como chave, em qualquer nível) **e** os **valores** de texto de
  ambos os arquivos por padrão de e-mail, CPF, CNPJ e telefone brasileiro (regex) — um contato real
  digitado dentro de `serviceDescription`, por exemplo, também derruba o teste, não só um campo com
  nome revelador.
- Coerência e reprodutibilidade do artefato de embeddings do corpus — ver "Reprodutibilidade do
  artefato — verificação executável", acima.
