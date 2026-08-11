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
| `embeddings/text-embedding-qwen3-embedding-0.6b.json` | Vetores pré-computados das descrições acima, indexados pelo hash do documento (`EmbeddingDocument.Hash`). | **Sim** — artefato gerado pela task T9 (gate humano), com o modelo decidido em `project/adr/ADR-002-provedor-de-embeddings.md` (harness). Gerado em 2026-08-10 (`bge-m3`); **regenerado em 2026-08-11 com `qwen3-embedding-0.6b`** — `project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md` (harness). |

## Procedência do artefato de vetores (T9; modelo trocado na MET-524)

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
- `embeddings/text-embedding-qwen3-embedding-0.6b.json`: suba o LM Studio servindo
  `text-embedding-qwen3-embedding-0.6b` (quantização `Q8_0`) em `http://localhost:1234/v1`, exporte
  `Embeddings__BaseUrl=http://localhost:1234/v1` e `Embeddings__Model=text-embedding-qwen3-embedding-0.6b`
  no shell (nenhuma chave necessária) e rode a ingestão real com `Embeddings__Provider=openai-compatible`
  apontando `ConnectionStrings__Prumo` para um banco descartável — `dotnet run --project
  src/Prumo.Seed` grava os 150 vetores reais (com `sourceHash` calculado por
  `EmbeddingDocument.Hash`, a mesma função usada na decisão de re-embedding) nas colunas
  `professionals.embedding*`. Depois, exporte essas colunas (`slug`, `embedding_source_hash`,
  `embedding`, mais `model`/`dimensions`/`hashAlgorithm` fixos) para o formato do artefato
  (`design.md` §5.3) — determinístico (ordenado por `slug`), sem timestamp, EOL LF. Trocar de
  modelo/quantização é o mesmo procedimento, com um nome de arquivo novo
  (`embeddings/<modelo>.json`) — o `model` gravado no artefato é o que detecta divergência entre
  arquivos (`PrecomputedEmbeddingStore.Load`).

## ⚠️ Aviso — o corpus é a régua do M1

O golden set de busca da MET-479 é medido **contra as descrições deste `professionals.json`**. Em
particular, o requisito ING-08 (≥ 20% das descrições sem o nome da especialidade nem os
`corpusSynonyms` declarados) é o que torna a busca semântica distinguível de uma busca por palavra-chave.

**Depois que o golden set da MET-479 estiver congelado, mudar qualquer descrição deste arquivo muda
a régua** — e passa a exigir ADR (`project/adr/README.md`: mudança no golden set é decisão do dono),
não uma edição silenciosa. Enquanto o golden set não existe, este corpus é livre para ajuste sem esse
custo (`spec.md`, seção "Medição do Case").

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
