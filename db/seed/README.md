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
| `embeddings/text-embedding-bge-m3.json` | Vetores pré-computados das descrições acima, indexados pelo hash do documento (`EmbeddingDocument.Hash`). | **Sim** — artefato gerado pela task T9 (gate humano), com o modelo decidido em `project/adr/ADR-002-provedor-de-embeddings.md` (harness). Gerado em 2026-08-10. |

## Procedência do artefato de vetores (T9)

- **Modelo:** `bge-m3` (BAAI), servido localmente pelo LM Studio como `text-embedding-bge-m3`
  (publisher `gpustack`, arquitetura `bert`, formato `gguf`) em `http://localhost:1234/v1`
  (endpoint OpenAI-compatible, decisão B da ADR-002).
- **Quantização:** `Q8_0` — confirmada via `GET /api/v0/models` do próprio LM Studio (API estendida
  que expõe o campo `quantization`), não presumida.
- **Dimensão:** 1024 — saída **nativa** do modelo (LM Studio ignora o parâmetro `dimensions` do
  request; `bge-m3` não é Matryoshka/truncável — ver D3 da `spec.md`, revista na MET-521). Casa com
  `embedding vector(1024)` (`db/migrations/0004_professional_embedding_dimension_1024.sql`).
- **`model` gravado no artefato e por linha em `professionals.embedding_model`:**
  `openai-compatible:text-embedding-bge-m3@1024` (formato de `IEmbeddingProvider.ModelId`, prefixo
  `openai-compatible` porque o mesmo cliente serve OpenAI e LM Studio — ver
  `OpenAiCompatibleEmbeddingProvider`).
- **Sem prefixo de instrução.** `bge-m3` **não é da família E5** (arquitetura `bert`, linhagem BAAI
  BGE-M3) e sua documentação (FlagEmbedding/BGE) não exige instrução/prefixo distinto para consulta
  vs. documento — ao contrário de modelos E5 (`query: `/`passage: `). Nenhum prefixo é aplicado na
  ingestão. Confirmado empiricamente na verificação de português da T9 (texto cru, sem prefixo,
  produziu vizinhança semântica correta nos 3 pares testados contra o corpus real, com margem
  confortável de similaridade sobre o distrator de especialidade errada em todos). **Se um modelo
  futuro exigir prefixo, isso precisa ser aplicado nos dois lados — ingestão e busca (MET-479) — de
  forma simétrica, e documentado aqui.**
- **Nenhuma credencial usada.** LM Studio local não exige `Embeddings__ApiKey`.
- **Determinismo confirmado bit-a-bit.** A geração foi repetida duas vezes contra o mesmo endpoint
  (LM Studio local, `bge-m3`/`Q8_0`) e comparada componente a componente: **0 de 153.600 valores
  divergentes** (150 vetores × 1024 dimensões, float32), independente do tamanho do lote de
  requisições. Regenerar hoje, nesta máquina, reproduz o artefato exatamente — não é só a
  normalização/hash que é determinística (isso já era garantido por construção,
  `EmbeddingDocument.Hash`); a inferência do próprio modelo também é.

## Como regenerar

- `specialties.json` e `professionals.json` **não têm gerador automático** — são dado versionado
  (D7). Editar é abrir o JSON e mudar à mão, respeitando o formato de
  `design.md` §5 e as constraints de `db/migrations/0002_specialties_and_professionals.sql`
  (formato de slug, `state` em duas maiúsculas, faixas de latitude/longitude/raio, descrição com
  pelo menos 40 caracteres).
- `embeddings/text-embedding-bge-m3.json`: suba o LM Studio servindo `text-embedding-bge-m3`
  (quantização `Q8_0`) em `http://localhost:1234/v1`, exporte
  `Embeddings__BaseUrl=http://localhost:1234/v1` e `Embeddings__Model=text-embedding-bge-m3` no
  shell (nenhuma chave necessária) e rode a ingestão real com `Embeddings__Provider=openai-compatible`
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
