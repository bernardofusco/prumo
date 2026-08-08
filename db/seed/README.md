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
| `embeddings/<modelo>.json` | Vetores pré-computados das descrições acima, indexados pelo hash do documento (`EmbeddingDocument.Hash`). | **Sim** — artefato gerado pela task T9 (gate humano, depende da decisão P1/ADR-002). Ainda não existe neste repo até T9 rodar. |

## Como regenerar

- `specialties.json` e `professionals.json` **não têm gerador automático** — são dado versionado
  (D7). Editar é abrir o JSON e mudar à mão, respeitando o formato de
  `design.md` §5 e as constraints de `db/migrations/0002_specialties_and_professionals.sql`
  (formato de slug, `state` em duas maiúsculas, faixas de latitude/longitude/raio, descrição com
  pelo menos 40 caracteres).
- `embeddings/<modelo>.json` é regenerado rodando `dotnet run --project src/Prumo.Seed` com
  `Embeddings__Provider=openai-compatible` (ou o provider decidido em P1) apontando para o
  `BaseUrl`/`Model` do provedor. O comando exato final é documentado no `README.md` da raiz do
  repo quando a T9 for concluída.

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
