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
├── src/Prumo.Api/          # Minimal API (.NET 10): health, busca (M1) e agenda/reservas (M2)
│   └── Agenda/             # SlotAvailability/ReservationDecision/LoadVerdict (puros) + Defenses/ (M2)
├── src/Prumo.Seed/         # Console de ingestão (M1): specialties/professionals + embeddings + slots de agenda (M2)
├── src/Prumo.Eval/         # Console que gera eval/embeddings/<modelo>.json (vetores das consultas do golden set)
├── src/Prumo.SeedEmbeddings/ # Console que gera db/seed/embeddings/<modelo>.json (vetores do corpus, sem banco)
├── tests/Prumo.Api.Tests/  # xUnit; Category=Integration usa Testcontainers; Eval/ mede o golden set
├── frontend/               # React + TypeScript (Vite) + Vitest — busca híbrida (M1) + agenda/reserva (M2)
├── db/migrations/          # SQL forward-only — as constraints são parte da história (0005 = agenda/reservas, M2)
├── db/seed/                # Corpus de demonstração 100% fictício (specialties.json, professionals.json)
├── db/seed/embeddings/     # Vetores REAIS pré-computados do corpus (versionado, ver "Sem provedor…")
├── eval/                   # As DUAS réguas do case: golden-set.json (M1) e concurrency-ledger.md (M2)
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
> 2026-08-08; artefato do corpus gerado na T9, 2026-08-10; **modelo trocado de `bge-m3` para
> `qwen3-embedding-0.6b` na MET-524, 2026-08-11 — `project/adr/ADR-004-modelo-de-embeddings-qwen3-e-revisao-gs-07.md`
> no repo do harness — via LM Studio local, sem custo, sem chave, mesma dimensão, sem migration**):
> `db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json` e
> `eval/embeddings/text-embedding-qwen3-embedding-0.6b.json` (consultas do golden set, T10) estão
> versionados e são o caminho padrão (`Embeddings__Provider=precomputed`). **A medição contra o
> golden set (T11) rodou duas vezes** (BSC-15..18) — a primeira, contra `bge-m3`, reprovou
> (`hitRate@3 = 0,75`); a segunda, contra `qwen3-embedding-0.6b` e com a consulta `gs-07` revisada,
> fechou L1 e L4 (`hitRate@3 = 1,00`) mas não fechava L2 contra o piso ORIGINAL da spec
> (`meanPrecision@5 ≥ 0,70` — o melhor ponto do grid mediu 0,41 naquela medição). Leave-one-out sobre
> o corpus real (150 descrições, cada uma como consulta contra as outras 149) mostrou por que:
> `meanPrecision@5 = 0,7173` mesmo no cenário mais favorável concebível — o teto é a distintividade do
> corpus entre especialidades, não o modelo nem a fórmula (ver `eval/README.md`, "Grid de calibração e
> limiares"). **O dono ratificou, via `project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md`
> (repo do harness, 2026-08-11), um piso `L2 = 0,40`** — derivado mecanicamente do valor medido no
> único ponto elegível a L1/L3 pela mesma regra de arredondamento que a spec já declarava — e os pesos
> que a regra de escolha da spec já apontava (`SemanticWeight = 0,9`, `ProximityWeight = 0,1`,
> `DistanceDecayKm = 5`), agora gravados em `appsettings.json` **e registrados como fato** no corpo de
> `project/adr/ADR-003-formula-do-ranking-hibrido.md` (repo do harness) — que **permanece `Proposed`**:
> a promoção formal a `Accepted` é decisão do dono, ainda não tomada (a ADR-005 é explícita em não a
> tomar em nome dele). **Vetores do corpus corrigidos (2026-08-11, MET-528).** 3 dos 150 vetores do
> artefato do corpus não eram reprodutíveis pelo modelo declarado
> (`project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md`, repo do harness) —
> regenerados e travados por verificação executável (`tests/Prumo.Api.Tests/SeedCorpusTests.cs` +
> `tests/Prumo.Api.Tests/EmbeddingsArtifactReproducibilityTests.cs`). A régua remedida:
> `meanPrecision@5 = 0,42` (era 0,41) no mesmo ponto adotado; **o piso `L2 = 0,40` NÃO mudou**
> (`floor(0,42 / 0,05) × 0,05 = 0,40` — a mesma regra, aplicada ao valor novo). **A régua do M1
> fecha**: L1, L2 (revisado), L3 e L4 satisfeitos. Buscar por uma das 150 descrições literais do
> corpus, ou por qualquer uma das ~20 consultas do golden set, funciona com vetor semântico real;
> texto livre arbitrário fora dessa lista responde `422` (ver seção
> "Busca" → "Sem provedor de embeddings configurado"). O agendamento sob concorrência é o M2. Este
> projeto é desenvolvido com o harness de agentes [`main-brain`](https://github.com/bernardofusco)
> (adapter `project-prumo`): specs aprovadas por humano, implementação por agentes com gates de
> build/teste, revisão independente e QA em browser real.

> **Atualização — M2 concluído (MET-480, agendamento sob concorrência).** A partir da busca, o
> cliente vê a agenda de um profissional e reserva um horário; o profissional publica/remove slots
> numa tela sem login (demonstração). Três defesas do mesmo invariante — constraint de exclusão
> (`EXCLUDE USING gist`, a oficial do produto), lock pessimista (`SELECT … FOR UPDATE`) e lock
> otimista (coluna `version`) — vivem lado a lado no repo, selecionáveis por `Scheduling:Defense`.
> A régua nomeada do milestone (`tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs`, N = 20
> tentativas simultâneas no mesmo horário) mede, para as três, **exatamente 1 sucesso e 19 recusas
> `409`/`slot_conflict`, zero `503`/`500`/timeout** — resultado versionado em
> [`eval/concurrency-ledger.md`](eval/concurrency-ledger.md). Ver a seção "Agendamento" abaixo para
> como rodar a demo, reproduzir a corrida (J2) e onde cada defesa mora no código.
> [`project/adr/ADR-007-ferramenta-do-teste-de-carga.md`](https://github.com/bernardofusco/project-prumo/blob/develop/project/adr/ADR-007-ferramenta-do-teste-de-carga.md)
> (repo do harness) fechou a ferramenta da régua (harness próprio em xUnit, zero dependência nova) e
> [`ADR-008`](https://github.com/bernardofusco/project-prumo/blob/develop/project/adr/ADR-008-deadlock-da-exclusao-e-conflito-de-negocio.md)
> registra um achado só a medição revelou: sob N = 20 no mesmo intervalo, o Postgres recusa as
> perdedoras por **deadlock (`40P01`)**, não por violação de exclusão (`23P01`) — as duas são
> traduzidas para `409` pelo mesmo tradutor de conflito.

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
export Embeddings__PrecomputedPaths__0=db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json
dotnet run --project src/Prumo.Seed
```

(se você mudou `POSTGRES_PORT`/usuário/senha no seu `.env`, ajuste `Port=`/`Username=`/`Password=` na
primeira linha para bater — mesmo aviso de `Port=5432` já feito na connection string do
`.env.example`.) **Desde a MET-480 (M2), o mesmo comando também publica a agenda de demonstração**
— nenhum passo/console separado: um quarto bloco no resumo, `Agenda: N slot(s) publicado(s), M
preservado(s) (reservado ou fora do gerenciado)`. Saída real de uma execução limpa (banco vazio):

```
Ingestão concluída.
  Especialidades: 15 criada(s), 0 atualizada(s).
  Profissionais:  150 criado(s), 0 atualizado(s).
  Embeddings:     150 gerado(s), 0 pulado(s) (já sincronizado(s)).
  Agenda:         60 slot(s) publicado(s), 0 preservado(s) (reservado ou fora do gerenciado).
```

Os 60 vêm de uma conta determinística, não medida à mão: 4 profissionais curados
(`AgendaSeedPlan.DefaultCuratedProfessionalSlugs`, incluindo `ana-oliveira-bh-001`, a encanadora da
jornada "vazamento no banheiro") × 5 dias úteis à frente × 3 janelas de 1h por dia
(`AgendaSeedPlan.BuildWindows`) — provado por `tests/Prumo.Api.Tests/Seed/AgendaSeedPlanTests.cs`
(sem banco) e por `tests/Prumo.Api.Tests/Integration/AgendaSeedTests.cs` (com Postgres real,
Testcontainers). "Preservado" só sobe se algum desses horários já tiver sido reservado por um
cliente entre duas execuções do seed — o passo nunca apaga um slot `source='seed'` com reserva, nem
qualquer slot `source='manual'` publicado pela tela de agenda (design.md §9 da MET-480).

Cada profissional fica com `embedding_model = openai-compatible:text-embedding-qwen3-embedding-0.6b@1024` —
vetores **reais** (`qwen3-embedding-0.6b`, via LM Studio local — modelo trocado na MET-524; gerados
uma vez, T9), não o placeholder determinístico. Rodar de novo é seguro e **não** duplica linha nem chama rede nenhuma à toa: o
comando faz upsert por `slug` (constraint `UNIQUE`, não um `SELECT` prévio) e só reembeda quando o
texto ou o provider configurado mudou — provado por `SeedIdempotencyTests` (segunda execução: zero
chamadas ao provider). Rodando o mesmo bloco de novo, a saída passa a ser:

```
Ingestão concluída.
  Especialidades: 0 criada(s), 15 atualizada(s).
  Profissionais:  0 criado(s), 150 atualizado(s).
  Embeddings:     0 gerado(s), 150 pulado(s) (já sincronizado(s)).
  Agenda:         60 slot(s) publicado(s), 0 preservado(s) (reservado ou fora do gerenciado).
```

(a agenda mostra `60 publicado(s)` de novo, não `0` — o passo apaga cada slot `seed` livre daquela
janela exata e o recria, deliberadamente, para que a grade continue rolante a partir de "amanhã" a
cada execução; só reservar um desses horários entre as duas execuções faria `Preservado` subir.)

Ao final, sai com código `0`; entrada inválida (arquivo de corpus ausente/malformado) sai com `2`,
falha do provedor de embeddings — inclusive `Embeddings:BaseUrl`/`Embeddings:Model` ausentes no
`openai-compatible` ou artefato ausente no `precomputed` — com `3`, falha de banco com `4` — nunca
com o valor de uma credencial na mensagem.

O provider é escolhido por `Embeddings__Provider` (ver `.env.example`), com três opções:

| Provider | Quando usar | Precisa de rede/chave? |
|---|---|---|
| `hashing` | Dev e testes rápidos, sem depender do artefato. Determinístico, sem rede, sem arquivo. **Não** é embedding semântico — não resolve "vazamento no banheiro → encanador"; existe para rodar todo o pipeline (schema, idempotência, consulta `<=>`) sem custo nenhum. **Não há default no código para este valor** — a variável precisa estar exportada. | Não |
| `precomputed` | **Caminho padrão desde a T9** (usado nos dois blocos acima): `db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json` já existe, versionado — lê o arquivo e resolve o vetor de cada documento pelo hash do texto. Se a descrição mudou sem o artefato ser regenerado, a ingestão falha alto (`exit 3`) em vez de gravar um vetor errado. | Não (mas exige o arquivo, via `Embeddings__PrecomputedPaths__0=...`, como acima) |
| `openai-compatible` | Foi assim que o artefato `precomputed` foi gerado (T9, LM Studio local servindo `qwen3-embedding-0.6b`) e é como se regenera (corpus mudou, ou troca de modelo — ver `db/seed/README.md`); também embeda a consulta do usuário em runtime na busca (seção "Busca", abaixo), quando o texto digitado não está no artefato. | Sim — `Embeddings__BaseUrl` e `Embeddings__Model` sempre; `Embeddings__ApiKey` só contra a OpenAI (LM Studio local não exige) |

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
export Embeddings__PrecomputedPaths__0=db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json
dotnet run --project src/Prumo.Seed                                 # seed (vetores reais, qwen3-embedding-0.6b)
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

**Estado real deste repo (pós-T10, MET-524 2026-08-11):** os dois artefatos —
`db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json` (CORPUS, 150 profissionais) e
`eval/embeddings/text-embedding-qwen3-embedding-0.6b.json` (as ~20 consultas do golden set) — existem
e são o default de `Embeddings__PrecomputedPaths__0`/`__1` em `.env.example`, com
`Embeddings__Provider=precomputed`. O que isso destrava e o que **não** destrava, sem chave nenhuma:

- **Digitar uma das 150 descrições de `db/seed/professionals.json` literalmente** (mesmo texto que
  gerou o vetor daquele profissional), ou qualquer uma das **~20 consultas de
  `eval/golden-set.json`** (inclusive as que `GET /api/search/options` devolve em `exampleQueries`),
  funciona: o hash bate no artefato, `embedding.mode: "precomputed"`, resultado semântico real. A
  busca do hash é **insensível à caixa** (MET-527) — `vazamento no banheiro`, `Vazamento no
  banheiro` e `VAZAMENTO NO BANHEIRO` batem no MESMO artefato e devolvem o MESMO resultado, o que
  importa porque teclado de celular capitaliza a primeira letra por padrão; só o texto realmente
  embeddado (nunca exibido em log) preserva a caixa que gerou o vetor.
- **Digitar texto livre** fora dessas listas responde **HTTP 422** `embedding_unavailable`, com a
  lista das consultas de demonstração no corpo — comportamento correto por design (D8), não bug.
- Para buscar por texto livre **arbitrário**, configure `Embeddings__Provider=openai-compatible`
  apontando para um LM Studio local (ou outro endpoint compatível) — a MESMA configuração usada para
  gerar os artefatos (ver seção "Seed").

#### A medição (o golden set)

O ranking é medido contra `eval/golden-set.json` — 20 consultas em linguagem de cliente, com
resultado esperado, versionadas no repo. O que a régua mede, os limiares, o grid de calibração e a
composição verificada por teste estão documentados em **[`eval/README.md`](eval/README.md)**. A
medição (`Category=Integration`, `tests/Prumo.Api.Tests/Integration/GoldenSetEvalTests.cs`) **rodou
duas vezes**: contra `bge-m3` (2026-08-10, reprovou — `hitRate@3 = 0,75`) e, depois da troca de
modelo e da revisão da consulta `gs-07` (MET-524, 2026-08-11, `project/adr/ADR-004-...md` no repo do
harness), contra `qwen3-embedding-0.6b` — que fechou L1 e L4 (`hitRate@3 = 1,00`) mas não fechava L2
contra o piso ORIGINAL da spec (`meanPrecision@5 ≥ 0,70`; melhor ponto do grid mediu 0,41 naquela
medição). O dono ratificou um piso menor (`L2 = 0,40`, arredondamento mecânico do valor medido) e os
pesos apontados pela regra de escolha (`w_s = 0,9`, `τ = 5`) via
`project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md` (repo do harness, 2026-08-11)
— **a régua do M1 fecha** com esses valores gravados em `appsettings.json`. **3 dos 150 vetores do
artefato do corpus se revelaram irreprodutíveis pelo modelo declarado e foram corrigidos** (MET-528,
2026-08-11, `project/adr/ADR-006-artefato-do-corpus-regenerado-para-ser-reproduzivel.md` no repo do
harness) — a régua remedida com o artefato corrigido: `meanPrecision@5 = 0,42` no ponto adotado
(era 0,41); o piso `L2 = 0,40` **não mudou** (a mesma regra de arredondamento, aplicada a 0,42,
continua dando 0,40). A tabela completa (18 pontos, só-semântica vs. híbrido), o racional da mudança
de piso e as mitigações contra sobreajuste estão em `eval/README.md`, "Grid de calibração e limiares".

**Só-semântica vs. híbrido vs. baseline lexical** — o resumo que sustenta a tese do case (tabela
completa dos 18 pontos, com todos os `τ`, em `eval/README.md`). As linhas "só-semântica" e "híbrido"
vêm da mesma suíte (`GoldenSetEvalTests`, `Category=Integration`) contra Postgres+pgvector real
(Testcontainers), vetores `qwen3-embedding-0.6b` (artefato corrigido, MET-528), reexecutadas para este
milestone (não copiadas de outro documento). A linha "baseline lexical" vem de um instrumento
diferente — `tests/Prumo.Api.Tests/Eval/LexicalBaseline.cs`, determinístico, sem banco e sem vetor (o
análogo direto de `WHERE description ILIKE '%palavra%'` somado por termo) — inalterada pela correção
de vetores (não depende de embedding nenhum) e conferida contra o número já publicado em
`eval/README.md`; `meanPrecision@5` e ordem não se aplicam a esse instrumento (colunas marcadas
abaixo):

| Ranking | `hitRate@3` | `meanPrecision@5` | ordem (`expectedRankedAbove`) | fecha L1 + L3? |
|---|---:|---:|---:|---|
| Baseline lexical (`LIKE '%palavra%'`, referência — sem vetor, `LexicalBaseline.cs`) | 0,60 (12/20) | não avaliado por esta métrica | não avaliado | não é candidato ao ranking |
| Só-semântica (`w_s = 1,0`, `w_p = 0`, qualquer `τ`) | 1,00 | 0,42 | 1/2 | **não** — falha L3 |
| **Híbrido — adotado** (`w_s = 0,9`, `w_p = 0,1`, `τ = 5`) | **1,00** | **0,42** | **2/2** | **sim** |

A diferença entre só-semântica e híbrido não aparece em `hitRate@3` (as duas acertam 100% das
consultas) nem em `meanPrecision@5` (empatadas em 0,42) — aparece na **ordem**: sem proximidade no
score, os dois pares `expectedRankedAbove` do golden set (`gs-16`, `gs-17`) não têm como ser
desempatados por distância, e a busca só-semântica reprova L3. É por isso que o ranking v1 é híbrido,
mesmo com um peso de proximidade pequeno (0,1): o suficiente para resolver a ordem sem alterar quantas
buscas acertam a especialidade certa. Contra a busca semântica (híbrida ou só-semântica), o baseline
lexical erra 8 das 20 consultas no top-3 — é a folga que prova que o golden set não está apenas
premiando quem repete a palavra da consulta.

`meanPrecision@5` (o quão "limpo" é o top-5 inteiro) tem piso próprio, revisado de 0,70 para 0,40
(ADR-005) — não porque o ranking piorou, mas porque a composição do corpus (15 especialidades de
vocabulário de "serviço doméstico" próximo entre si) tem um teto medido de ≈ 0,61 mesmo no melhor caso
possível (leave-one-out sobre as 150 descrições reais do corpus); ver `eval/README.md`, "Piso `L2`
baixado de 0,70 para 0,40", para a medição completa e o racional.

### Agendamento (MET-480 — reserva sob concorrência)

Com o banco populado (seção "Seed" — o mesmo comando já publica profissionais **e** uma grade
rolante de slots de agenda, ver abaixo) e a API + frontend no ar (seção "API e frontend"), a agenda
já funciona de ponta a ponta:

1. Busque algo como "vazamento no banheiro" (`http://localhost:5173`) — a busca da MET-479
   continua exatamente como era.
2. No card de um resultado, acione **"Ver horários"** (teclado incluso — `Tab`/`Enter`, tem nome
   acessível). A tela vai para `/profissional/{slug}` e lista os horários do profissional com o
   `status` que a API já calculou (`available`/`booked`/`past` — o React nunca decide isso sozinho).
3. Clique em **"Reservar"** num horário `available`. A confirmação aparece numa região `aria-live`
   persistente; o mesmo horário passa a `booked` na próxima leitura.
4. Em **"Configurar horários desta agenda (demonstração sem login)"**, o profissional publica
   (`start`/`end`) e remove slots — **sem autenticação nenhuma**: quem tiver a URL
   `/profissional/{slug}/agenda` age como se fosse aquele profissional (decisão deliberada de
   escopo de demo, spec.md D2 da MET-480; a tela mostra um aviso permanente disso, não um toast que
   some).

O seed curado inclui `ana-oliveira-bh-001` (encanadora) com slots publicados — é o profissional que
a jornada acima encontra digitando "vazamento no banheiro".

#### Reproduzindo a corrida entre dois clientes (jornada J2)

A identidade do cliente é um UUID anônimo (`prumo.clientKey`) gerado no `localStorage` na primeira
visita (spec.md D2 — sem login, sem cookie de sessão, sem dado pessoal). Para repetir a "corrida"
que é a história central do M2 — dois clientes tentando fechar o mesmo horário — você precisa de
**duas `clientKey` distintas**, ou seja, dois "clientes" diferentes no mesmo navegador:

1. Reserve um horário normalmente (passos 1–3 acima) numa aba comum.
2. Abra uma **janela anônima/privada** do navegador (ou, na mesma aba,
   `localStorage.removeItem('prumo.clientKey')` pelo DevTools e recarregue a página — as duas formas
   geram uma `clientKey` nova) e vá para o **mesmo** `/profissional/{slug}`.
3. Tente reservar o **mesmo** horário que já foi reservado no passo 1. **Esperado:** `409` —
   mensagem clara de conflito ("esse horário acabou de ser reservado por outro cliente"), nunca o
   texto de "tente novamente" de um `503`. Atualizando a lista, o slot aparece `booked`.

Para ver a régua de verdade — **N = 20** tentativas simultâneas, não só duas — não é preciso abrir
20 janelas: é exatamente o que
[`tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs`](tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs)
automatiza contra as três defesas (seção "A régua da concorrência", abaixo).

#### As três defesas, e como alternar (`Scheduling:Defense`)

A mesma interface (`IReservationDefense`, `src/Prumo.Api/Agenda/Defenses/IReservationDefense.cs`)
tem três implementações, lado a lado no repo — a tese do case é que elas resolvem o **mesmo**
invariante (no máximo uma reserva por intervalo do profissional) por mecanismos diferentes:

| Defesa | Arquivo | Mecanismo |
|---|---|---|
| `exclusion` (**oficial do produto**, default) | `Agenda/Defenses/ExclusionDefense.cs` | `INSERT` e deixa o banco decidir — `EXCLUDE USING gist (professional_id WITH =, period WITH &&)` na migration `0005`. Nenhuma checagem de overlap em código. |
| `pessimistic` | `Agenda/Defenses/PessimisticDefense.cs` | `SELECT … FOR UPDATE` na linha do slot antes de decidir — serializa os concorrentes na aplicação. |
| `optimistic` | `Agenda/Defenses/OptimisticDefense.cs` | `UPDATE availability_slots SET version = version + 1 WHERE id = @id AND version = @v` — CAS pela coluna `version`; 0 linhas afetadas ⇒ conflito. |

O caminho HTTP de produção/demo (`POST /api/reservations`) **sempre** usa a defesa configurada em
`Scheduling:Defense` (`src/Prumo.Api/appsettings.json`, default `"exclusion"`) — a UI nunca escolhe
qual defesa está ativa. Para rodar a API localmente com outra defesa (só para inspeção/comparação
manual — a demo pública continua em `exclusion`):

```sh
export Scheduling__Defense=pessimistic   # ou optimistic
dotnet run --project src/Prumo.Api
```

Um valor fora de `exclusion`/`pessimistic`/`optimistic` derruba o boot da API
(`ValidateOnStart`) — não falha em silêncio na primeira reserva.

#### A régua da concorrência (a segunda régua do case)

`dotnet test --filter "Category=Integration"` inclui o teste de carga do M2
(`ConcurrencyLoadTests`): **N = 20** `POST /api/reservations` simultâneos, via
`WebApplicationFactory` + Postgres real, no **mesmo** horário do **mesmo** profissional, com 20
`clientKey` distintas — repetido uma vez por defesa, cada rodada com um slot próprio. A régua
(`Prumo.Api.Agenda.Scheduling.LoadVerdict`) só passa com **exatamente 1** sucesso de criação e
**19** recusas `409`/`slot_conflict` — zero de qualquer outro status (`200` replay, `422`, `500`,
`503`, timeout). O resultado medido — não escrito à mão — é publicado em
[`eval/concurrency-ledger.md`](eval/concurrency-ledger.md), ao lado do golden set do M1
(`eval/README.md`). Mudar N ou o critério "exatamente 1 sucesso" é ADR + decisão do dono
(`project/adr/ADR-007-ferramenta-do-teste-de-carga.md`,
`project/adr/ADR-008-deadlock-da-exclusao-e-conflito-de-negocio.md`, repo do harness).

#### Limitação conhecida: rotas profundas fora do dev server

`/profissional/:slug` e `/profissional/:slug/agenda` são resolvidas pelo **fallback SPA do
servidor de desenvolvimento do Vite** (`npm --prefix frontend run dev`): qualquer caminho que não
seja um arquivo estático cai em `index.html`, e o roteamento sem biblioteca (`lib/view.ts`, History
API — `react-router` está fora de escopo, spec.md D6 da MET-480) assume dali. **Isso não existe**
em `vite preview` nem servindo `frontend/dist/` como arquivos estáticos puros (ex.: atrás de um
Nginx sem regra de fallback, ou abrindo `dist/index.html` via `file://`): recarregar a página numa
URL profunda, ou colar o link direto, devolve 404 do servidor de arquivos — a navegação **dentro**
do app (clicar em "Ver horários") continua funcionando normalmente, só o acesso direto/recarga é
que quebra fora do dev server. A demo deste repo é o dev server; nenhum plugin de fallback foi
adicionado para cobrir um build estático/produção (design.md §10 da MET-480, fora de escopo v1 —
não há deploy neste milestone).

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
