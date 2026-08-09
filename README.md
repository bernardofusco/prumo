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

> **Estado atual:** fundação do M0 concluída, e o M1 (MET-478 — modelagem e ingestão) entregou o
> schema de domínio (`specialties`, `professionals`, coluna vetorial `embedding vector(768)` com
> procedência auditável), o corpus sintético (~150 profissionais) e o comando de ingestão idempotente
> (`src/Prumo.Seed`). A **busca com ranking híbrido medido contra o golden set** (MET-479) — a parte
> que expõe endpoint HTTP e UI de busca — ainda não existe; o agendamento sob concorrência é o M2. A
> escolha do provedor de embeddings real (API paga vs. modelo local) está registrada na ADR-002 do
> harness de agentes e é decisão pendente do dono — enquanto isso, tudo neste repo roda com o
> provider `hashing` (determinístico, sem rede, sem chave). Este projeto é
> desenvolvido com o harness de agentes [`main-brain`](https://github.com/bernardofusco) (adapter
> `project-prumo`): specs aprovadas por humano, implementação por agentes com gates de build/teste,
> revisão independente e QA em browser real.

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
```

(as variáveis `$POSTGRES_USER`/`$POSTGRES_DB` precisam ser expandidas **dentro** do container —
onde o compose as injeta via `environment:` — por isso o `sh -c` envolvendo o `psql`; expandi-las
no shell do host não funciona, pois o host não tem essas variáveis.) Ambas as migrations são
idempotentes no sentido operacional — reaplicá-las num banco que já as tem não falha nem duplica
constraint (provado por `tests/Prumo.Api.Tests/Integration/MigrationIdempotencyTests.cs`), então
rodar os dois comandos acima também é seguro se você não tiver certeza do que já foi aplicado.

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
**do processo**, então as duas variáveis que ele exige (`ConnectionStrings__Prumo` e
`Embeddings__Provider` — nenhuma das duas tem default no código, de propósito: falhar cedo e alto é
melhor que adivinhar) precisam estar exportadas no shell antes do `dotnet run`. Copiando os valores
dev-only de `.env.example`:

```sh
export ConnectionStrings__Prumo="Host=localhost;Port=5432;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me"
export Embeddings__Provider=hashing
dotnet run --project src/Prumo.Seed
```

(se você mudou `POSTGRES_PORT`/usuário/senha no seu `.env`, ajuste `Port=`/`Username=`/`Password=` na
primeira linha para bater — mesmo aviso de `Port=5432` já feito na connection string do
`.env.example`.) Saída real de uma execução limpa:

```
Ingestão concluída.
  Especialidades: 15 criada(s), 0 atualizada(s).
  Profissionais:  150 criado(s), 0 atualizado(s).
  Embeddings:     150 gerado(s), 0 pulado(s) (já sincronizado(s)).
```

Rodar de novo é seguro e **não** duplica linha nem chama o provedor de embeddings à toa: o comando
faz upsert por `slug` (constraint `UNIQUE`, não um `SELECT` prévio) e só reembeda quando o texto ou o
provider configurado mudou — provado por `SeedIdempotencyTests` (segunda execução: zero chamadas ao
provider). Rodando o mesmo bloco de novo, a saída passa a ser:

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
| `hashing` | Dev e testes — é o que os dois blocos acima usam. Determinístico, sem rede, sem arquivo. **Não** é embedding semântico — não resolve "vazamento no banheiro → encanador"; existe para rodar todo o pipeline (schema, idempotência, consulta `<=>`) sem custo nenhum. **Não há default no código para este valor** — a variável precisa estar exportada, como acima. | Não |
| `precomputed` | Caminho previsto para quem clona o repo **depois** que `db/seed/embeddings/<modelo>.json` existir (artefato gerado pela task de gate humano da MET-478, ainda pendente — ver `db/seed/README.md`): lê o arquivo versionado e resolve o vetor de cada documento pelo hash do texto. Se a descrição mudou sem o artefato ser regenerado, a ingestão falha alto (`exit 3`) em vez de gravar um vetor errado. | Não (mas exige o arquivo, também via `Embeddings__PrecomputedPaths__0=...`) |
| `openai-compatible` | Gera o artefato `precomputed` (rodando uma única vez contra a OpenAI ou um LM Studio local — mesmo cliente HTTP para os dois, muda só `BaseUrl`/`Model`/chave) e, futuramente, embeda a consulta do usuário em runtime na busca (MET-479). | Sim — `Embeddings__BaseUrl` e `Embeddings__Model` sempre; `Embeddings__ApiKey` só contra a OpenAI (LM Studio local não exige) |

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
