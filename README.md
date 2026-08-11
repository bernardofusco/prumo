# Prumo

**Busca e agendamento de profissionais liberais** — um case de engenharia em .NET 10 + React que
conta duas histórias afiadas em vez de um marketplace completo:

1. **Busca com ranking híbrido** — "vazamento no banheiro" encontra encanador sem conter a palavra
   "encanador". Embeddings + [pgvector](https://github.com/pgvector/pgvector) + filtro por raio +
   ranking com pesos explicáveis, **medido contra um golden set** de consultas.
2. **Agendamento sob concorrência** — dois clientes fecham o mesmo horário ao mesmo tempo. Três
   defesas comparadas (lock pessimista, lock otimista, **constraint de exclusão**) sob carga real
   via Testcontainers. A tese: *regra de integridade é constraint de banco, não convenção de
   código* — exatamente 1 sucesso sob N tentativas simultâneas, com teste que prova.

## Stack

- **Backend:** .NET 10 (ASP.NET Core Minimal APIs)
- **Frontend:** React + TypeScript (Vite)
- **Banco:** PostgreSQL + pgvector (+ `earthdistance` para filtro por raio)
- **Testes:** xUnit + Testcontainers (Postgres real em container) · Vitest no frontend
- **CI:** GitHub Actions

## Estrutura

```
prumo/
├── global.json             # SDK band do .NET fixada (CI reproduzível)
├── Prumo.sln
├── Directory.Build.props   # net10.0, Nullable, TreatWarningsAsErrors, EnforceCodeStyleInBuild
├── .editorconfig           # fonte da verdade do `dotnet format`
├── .gitattributes          # normaliza EOL (LF) independente do SO de quem clona
├── src/Prumo.Api/          # Minimal API (.NET 10): GET /api/health, GET /api/health/db
├── src/Prumo.Seed/         # Console de ingestão (M1): popula specialties/professionals + embeddings
├── tests/Prumo.Api.Tests/  # xUnit; Category=Integration usa Testcontainers
├── frontend/               # React + TypeScript (Vite) + Vitest — indicador de status da API
├── db/migrations/          # SQL forward-only — as constraints são parte da história
├── db/seed/                # Corpus de demonstração 100% fictício (specialties.json, professionals.json)
├── compose.yaml            # Postgres + pgvector (+ cube/earthdistance) para dev local
├── .env.example            # nomes de variáveis (nunca valores reais)
└── .github/workflows/ci.yml  # CI (espelha os gates do harness)
```

> **Estado atual:** fundação do M0 concluída; o M1 entregou modelagem e ingestão (MET-478) — schema
> de domínio (`specialties`, `professionals`, coluna vetorial `embedding vector(1024)` com procedência
> auditável), corpus sintético (~150 profissionais) e comando de ingestão idempotente
> (`src/Prumo.Seed`) — e a **busca com ranking híbrido** (MET-479): endpoint `GET /api/search` +
> `GET /api/search/options`, a função pura de ranking (`Search/Ranking/HybridRanker`), a recuperação
> de candidatos via `<=>`/`earthdistance` e a tela em React estão implementados e testados (ver seção
> "Busca", abaixo). **Provedor de embeddings decidido e gerado** (ADR-002, `Accepted` desde
> 2026-08-08; artefato do corpus gerado na T9, 2026-08-10 — `bge-m3` via LM Studio local, sem custo,
> sem chave): `db/seed/embeddings/text-embedding-bge-m3.json` está versionado e é o caminho padrão
> (`Embeddings__Provider=precomputed`). O que ainda falta é a **medição contra o golden set**
> (BSC-15..18) e a **ratificação dos pesos** (BSC-20/21, ADR-003) — as duas dependem do artefato de
> CONSULTAS do golden set (`eval/embeddings/<modelo>.json`, MET-479/T10, ainda não gerado), não mais
> de nenhuma decisão de provedor pendente. Até T10 rodar, buscar por uma das 150 descrições literais
> do corpus funciona com vetor semântico real; texto livre arbitrário responde `422` (ver seção
> "Busca" → "Sem provedor de embeddings configurado"). O agendamento sob concorrência é o M2. Este
> projeto é desenvolvido com o harness de agentes [`main-brain`](https://github.com/bernardofusco)
> (adapter `project-prumo`): specs aprovadas por humano, implementação por agentes com gates de
> build/teste, revisão independente e QA em browser real.

## Como rodar

Pré-requisitos: [Docker](https://docs.docker.com/get-docker/) (para o banco e para os testes de
integração), .NET (SDK fixado em `global.json`) e Node (versão fixada em `frontend/.nvmrc`).

### Banco (Postgres + pgvector)

```sh
cp .env.example .env      # nomes de variáveis com defaults dev-only; nunca versione o .env
docker compose up -d      # sobe Postgres com vector, cube e earthdistance (migration 0001)
```

O container fica `healthy` quando o `pg_isready` responde. As extensões são criadas pela
migration `db/migrations/0001_extensions.sql`, montada (somente leitura) em
`/docker-entrypoint-initdb.d/` — o Postgres oficial só executa esse diretório **na primeira
inicialização do volume**.

**Aplicar uma migration nova em um banco já existente** (volume já inicializado, então
`/docker-entrypoint-initdb.d/` não roda de novo): o diretório `db/migrations/` já está montado
dentro do container, então basta apontar o `psql` para o(s) arquivo(s) novo(s) lá dentro, **em
ordem**. Por exemplo, para levar um banco que só tem a migration `0001` (M0) até o schema atual do
M1 (`specialties`, `professionals` e a coluna vetorial):

```sh
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /docker-entrypoint-initdb.d/0002_specialties_and_professionals.sql'
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /docker-entrypoint-initdb.d/0003_professional_embeddings.sql'
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /docker-entrypoint-initdb.d/0004_professional_embedding_dimension_1024.sql'
```

(as variáveis `$POSTGRES_USER`/`$POSTGRES_DB` precisam ser expandidas **dentro** do container —
onde o compose as injeta via `environment:` — por isso o `sh -c` envolvendo o `psql`; expandi-las
no shell do host não funciona, pois o host não tem essas variáveis.) As três migrations são
idempotentes no sentido operacional — reaplicá-las num banco que já as tem não falha nem duplica
constraint (provado por `tests/Prumo.Api.Tests/Integration/MigrationIdempotencyTests.cs`), então
rodar os três comandos acima também é seguro se você não tiver certeza do que já foi aplicado.

**Resetar o banco do zero** (descarta os dados do container local — sempre sintéticos, nunca dado
real):

```sh
docker compose down -v
```

### Seed (popular o banco)

> **Dados 100% fictícios.** `db/seed/specialties.json` e `db/seed/professionals.json` são um corpus
> sintético de demonstração (~150 profissionais, nomes inventados, coordenadas de centros de
> cidades reais de MG/RJ/SP com deslocamento já aplicado offline) — nenhuma pessoa, empresa ou
> contato real. Detalhes em `db/seed/README.md`.

Com o banco no ar (`docker compose up -d`), popule `specialties`/`professionals` e vetorize a
descrição de serviço de cada profissional. **`dotnet run` não carrega `.env`** — só o
`docker compose` consome esse arquivo diretamente; o Seed lê configuração de variável de ambiente
**do processo**, então as variáveis que ele exige (`ConnectionStrings__Prumo`, `Embeddings__Provider`
e, no caminho padrão abaixo, `Embeddings__PrecomputedPaths__0` — nenhuma tem default no código, de
propósito: falhar cedo e alto é melhor que adivinhar) precisam estar exportadas no shell antes do
`dotnet run`. Copiando os valores dev-only de `.env.example` (caminho padrão desde a T9 — vetores
reais, sem chave, sem LM Studio no ar):

```sh
export ConnectionStrings__Prumo="Host=localhost;Port=5432;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me"
export Embeddings__Provider=precomputed
export Embeddings__PrecomputedPaths__0=db/seed/embeddings/text-embedding-bge-m3.json
dotnet run --project src/Prumo.Seed
```

(se você mudou `POSTGRES_PORT`/usuário/senha no seu `.env`, ajuste `Port=`/`Username=`/`Password=` na
primeira linha para bater — mesmo aviso de `Port=5432` já feito na connection string do
`.env.example`.) Saída real de uma execução limpa (banco vazio):

```
Ingestão concluída.
  Especialidades: 15 criada(s), 0 atualizada(s).
  Profissionais:  150 criado(s), 0 atualizado(s).
  Embeddings:     150 gerado(s), 0 pulado(s) (já sincronizado(s)).
```

Cada profissional fica com `embedding_model = openai-compatible:text-embedding-bge-m3@1024` —
vetores **reais** (`bge-m3`, gerados uma vez via LM Studio local, T9), não o placeholder
determinístico. Rodar de novo é seguro e **não** duplica linha nem chama rede nenhuma à toa: o
comando faz upsert por `slug` (constraint `UNIQUE`, não um `SELECT` prévio) e só reembeda quando o
texto ou o provider configurado mudou — provado por `SeedIdempotencyTests` (segunda execução: zero
chamadas ao provider). Rodando o mesmo bloco de novo, a saída passa a ser:

```
Ingestão concluída.
  Especialidades: 0 criada(s), 15 atualizada(s).
  Profissionais:  0 criado(s), 150 atualizado(s).
  Embeddings:     0 gerado(s), 150 pulado(s) (já sincronizado(s)).
```

Ao final, sai com código `0`; entrada inválida (arquivo de corpus ausente/malformado) sai com `2`,
falha do provedor de embeddings — inclusive `Embeddings:BaseUrl`/`Embeddings:Model` ausentes no
`openai-compatible` ou artefato ausente no `precomputed` — com `3`, falha de banco com `4` — nunca
com o valor de uma credencial na mensagem.

O provider é escolhido por `Embeddings__Provider` (ver `.env.example`), com três opções:

| Provider | Quando usar | Precisa de rede/chave? |
|---|---|---|
| `hashing` | Dev e testes rápidos, sem depender do artefato. Determinístico, sem rede, sem arquivo. **Não** é embedding semântico — não resolve "vazamento no banheiro → encanador"; existe para rodar todo o pipeline (schema, idempotência, consulta `<=>`) sem custo nenhum. **Não há default no código para este valor** — a variável precisa estar exportada. | Não |
| `precomputed` | **Caminho padrão desde a T9** (usado nos dois blocos acima): `db/seed/embeddings/text-embedding-bge-m3.json` já existe, versionado — lê o arquivo e resolve o vetor de cada documento pelo hash do texto. Se a descrição mudou sem o artefato ser regenerado, a ingestão falha alto (`exit 3`) em vez de gravar um vetor errado. | Não (mas exige o arquivo, via `Embeddings__PrecomputedPaths__0=...`, como acima) |
| `openai-compatible` | Foi assim que o artefato `precomputed` foi gerado (T9, LM Studio local servindo `bge-m3`) e é como se regenera (corpus mudou, ou troca de modelo — ver `db/seed/README.md`); também embeda a consulta do usuário em runtime na busca (seção "Busca", abaixo), quando o texto digitado não está no artefato. | Sim — `Embeddings__BaseUrl` e `Embeddings__Model` sempre; `Embeddings__ApiKey` só contra a OpenAI (LM Studio local não exige) |

Dois caminhos apontam para arquivos alternativos em vez do corpus real de `db/seed/` — pensados para
teste apontar para uma fixture própria sem escrever no corpus versionado, mas disponíveis como
override de configuração para qualquer execução: `Seed__SpecialtiesPath` e `Seed__ProfessionalsPath`
(deixe-as sem exportar, ou exportadas vazias — as duas formas caem no default do código,
`db/seed/specialties.json` e `db/seed/professionals.json` — não costumam precisar de ajuste em uso
normal).

### Consulta de similaridade (inspeção manual)

Depois de popular o banco (seção acima), a similaridade por cosseno usada pelo M1 pode ser
inspecionada direto via `psql`, com o operador `<=>` do pgvector ordenando por distância (menor =
mais próximo):

```sh
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "
  SELECT s.slug AS specialty_slug, p.slug AS professional_slug, p.full_name
  FROM professionals p
  JOIN specialties s ON s.id = p.specialty_id
  WHERE p.embedding IS NOT NULL
  ORDER BY p.embedding <=> (SELECT embedding FROM professionals WHERE slug = '\''ana-oliveira-bh-001'\'')
  LIMIT 5;
"'
```

Isso lista os 5 profissionais mais próximos, por embedding, de `ana-oliveira-bh-001` (encanadora do
corpus de demonstração) — ela mesma aparece em primeiro lugar (distância 0). Trocar a subconsulta por
um vetor arbitrário (`'[...]'::vector`) permite comparar contra uma descrição de teste em vez de um
profissional já existente — é exatamente o que
`tests/Prumo.Api.Tests/Integration/SimilaritySmokeTests.cs` faz de forma automatizada: prova, com o
provider `hashing` (determinístico, sem rede), que o vizinho mais próximo de 3 consultas versionadas
pertence à especialidade esperada. Esse teste mede o *pipeline* (persistência do vetor, dimensão,
operador `<=>`, corpus real) — não a qualidade semântica de um provedor real, que é medida pelo
golden set da MET-479.

### API e frontend

```sh
dotnet run --project src/Prumo.Api      # API em porta fixa (ver Properties/launchSettings.json)
npm --prefix frontend run dev           # Vite; server.proxy encaminha /api para a API (vite.config.ts)
```

Abra `http://localhost:5173`: o indicador de status parte de "verificando…" e vira "API online" (ou
"API indisponível", se a API/banco estiverem fora) em poucos segundos.

> **Build de produção / deploy cross-origin:** a API ainda não tem política de CORS configurada.
> Enquanto frontend e API forem servidos da mesma origem (dev via proxy do Vite), não há problema —
> mas isso precisa ser resolvido **antes** de qualquer deploy que sirva frontend e API de origens
> diferentes (ver comentário em `.env.example`, `VITE_API_BASE_URL`). Não implementado ainda: não há
> deploy neste milestone.

### Busca (MET-479 — ranking híbrido)

Com o banco populado (seção "Seed", acima) e a API + frontend no ar (seção anterior), a busca já
funciona de ponta a ponta: abra `http://localhost:5173`, digite uma descrição do serviço que precisa
(ou clique numa das consultas de demonstração) e submeta (Enter ou o botão — a busca nunca dispara a
cada tecla). Resumindo o fluxo completo do zero:

```sh
cp .env.example .env && docker compose up -d                       # banco
export ConnectionStrings__Prumo="Host=localhost;Port=5432;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me"
export Embeddings__Provider=precomputed
export Embeddings__PrecomputedPaths__0=db/seed/embeddings/text-embedding-bge-m3.json
dotnet run --project src/Prumo.Seed                                 # seed (vetores reais, bge-m3)
dotnet run --project src/Prumo.Api                                  # API em http://localhost:5096
npm --prefix frontend run dev                                       # frontend em http://localhost:5173
```

Cada resultado mostra nome, especialidade, cidade, **distância** (quando há localização) e o
**score**, com a decomposição já calculada pela API (relevância × peso + proximidade × peso) — o
React só formata e desenha; nenhuma aritmética de ranking acontece no frontend.

#### Os três estados de localização

- **"Usar minha localização"** — pede a posição ao navegador (`navigator.geolocation`); a coordenada
  é arredondada a 3 casas decimais (~110 m) **antes** de sair do browser (minimização de dado — não
  muda o ranking). Se a permissão for negada (ou o navegador não suportar), a tela mostra uma
  mensagem clara e leva o foco ao seletor de cidade, sem erro técnico nem travamento.
- **Cidade manual** — um `<select>` com as cidades do corpus (centroide calculado a partir dos
  próprios profissionais, sem geocodificador externo), vindo de `GET /api/search/options`.
- **Sem localização** — o padrão. A busca ordena só por relevância semântica; a lista de resultados
  não mostra distância, e um aviso permanente (não-dispensável) avisa que a proximidade não está
  sendo considerada.

Em qualquer um dos três estados, um profissional só aparece se **ele** atende aquele ponto
(`service_radius_km`) — o raio pedido pelo cliente (`radiusKm`) é parâmetro de API opcional, sem
controle correspondente na tela v1 (D5/P4 da spec).

#### Sem provedor de embeddings configurado (D8)

A demo pública roda **sem chave nenhuma**. O comportamento de `GET /api/search` depende só de
`Embeddings:Provider` (a mesma variável da ingestão, seção "Seed"):

| `Embeddings:Provider` | Consulta com vetor pré-computado | Consulta sem vetor pré-computado |
|---|---|---|
| `precomputed` | responde normalmente, `embedding.mode: "precomputed"` | **HTTP 422** `embedding_unavailable`, com a lista de consultas de demonstração no corpo |
| `openai-compatible` | usa o artefato pré-computado (grátis, determinístico), `embedding.mode: "precomputed"` | chama o provedor configurado, `embedding.mode: "provider"` |
| `hashing` | — (o modo degradado não olha o artefato) | usa o provider local determinístico, `embedding.mode: "degraded"`, com aviso visível na tela |

**Estado real deste repo (pós-T9, 2026-08-10):** `db/seed/embeddings/text-embedding-bge-m3.json` (o
artefato do CORPUS — 150 profissionais) existe e é o default de `Embeddings__PrecomputedPaths__0`
em `.env.example`, com `Embeddings__Provider=precomputed`. O que isso destrava e o que **não**
destrava, sem chave nenhuma:

- **Digitar uma das 150 descrições de `db/seed/professionals.json` literalmente** (mesmo texto que
  gerou o vetor daquele profissional) funciona: o hash bate no artefato, `embedding.mode:
  "precomputed"`, resultado semântico real — é como inspecionar o pipeline ponta a ponta sem chave.
- **Digitar texto livre** (ex.: "vazamento no banheiro", ou qualquer coisa que não seja uma dessas
  150 descrições exatas) responde **HTTP 422** `embedding_unavailable`: o artefato de CONSULTAS do
  golden set (`eval/embeddings/<modelo>.json`) nasce só na MET-479/T10, e sem ele não há vetor pra
  nenhum texto arbitrário. **`GET /api/search/options` também devolve `exampleQueries: []`** hoje —
  as "consultas de demonstração" citadas na mensagem de erro ainda não existem para clicar; T10 é
  quem preenche as duas coisas juntas (mesmo artefato).
- Para buscar por texto livre **agora**, configure `Embeddings__Provider=openai-compatible` apontando
  para um LM Studio local (ou outro endpoint compatível) — a MESMA configuração usada para gerar o
  artefato do corpus (ver seção "Seed").

#### A medição (o golden set)

O ranking é medido contra `eval/golden-set.json` — 20 consultas em linguagem de cliente, com
resultado esperado, versionadas no repo. O que a régua mede, os limiares, o grid de calibração e a
composição verificada por teste estão documentados em **[`eval/README.md`](eval/README.md)**; a
tabela de resultados (só-semântica vs. híbrido, os 18 pontos do grid, os pesos escolhidos) fica **em
branco até a medição rodar de verdade** — não mais por falta de decisão de provedor (ADR-002 é
`Accepted` desde 2026-08-08, `bge-m3` via LM Studio local, artefato do corpus já versionado, T9), mas
porque o artefato de **consultas** do golden set (`eval/embeddings/<modelo>.json`) e a medição em si
são a MET-479/T10, ainda não executada. Sem ele, o teste do golden set contra Postgres
(`Category=Integration`, `GoldenSetEvalTests`) não existe ainda neste repo: a régua está pronta
(consultas, métricas, conformidade), mas não há número medido para publicar aqui até T10 rodar.

### Testes e gates

Comandos a partir da raiz do repo (mesmos comandos que o CI e o harness (`harness.manifest.yaml →
gates`) executam):

```sh
dotnet format --verify-no-changes --no-restore                   # FORMAT
dotnet build -c Debug --nologo                                   # LINT (warnings são erros)
dotnet test --nologo -v minimal --filter "Category!=Integration" # TEST (sem Docker)
npm --prefix frontend run lint                                   # WEB_LINT
npm --prefix frontend run test                                   # WEB_TEST (vitest run, não-interativo)
npm --prefix frontend run build                                  # WEB_BUILD (tsc + vite build)

# Requer Docker: sobe Postgres+pgvector real via Testcontainers (mesma imagem do compose.yaml)
dotnet test --nologo -v minimal --filter "Category=Integration"  # INTEGRATION
```

## Princípios

- **Dados 100% sintéticos** — todo profissional, cliente, endereço e telefone em seed/fixture é
  inventado.
- **Sem segredos no repositório** — `.env.example` documenta os nomes das variáveis; valores nunca
  entram no histórico.
- **As medições não se ajustam para passar** — o golden set da busca e o teste de carga da reserva
  são a régua do projeto.

## Licença e caso de estudo

O case study completo (decisões, alternativas comparadas, números) será publicado em
[bernardofusco.dev](https://bernardofusco.dev) ao final do projeto.
