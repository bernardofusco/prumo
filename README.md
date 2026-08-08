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
├── tests/Prumo.Api.Tests/  # xUnit; Category=Integration usa Testcontainers
├── frontend/               # React + TypeScript (Vite) + Vitest — indicador de status da API
├── db/migrations/          # SQL forward-only — as constraints são parte da história
├── compose.yaml            # Postgres + pgvector (+ cube/earthdistance) para dev local
├── .env.example            # nomes de variáveis (nunca valores reais)
└── .github/workflows/ci.yml  # CI (espelha os gates do harness)
```

> **Estado atual:** fundação do M0 concluída — solution .NET, frontend, banco com pgvector/
> earthdistance, testes (unitários e de integração via Testcontainers) e CI estão no ar e verdes.
> A modelagem de domínio (profissionais, busca com ranking híbrido, agendamento) é dos próximos
> milestones (M1/M2). Este projeto é desenvolvido com o harness de agentes
> [`main-brain`](https://github.com/bernardofusco) (adapter `project-prumo`): specs aprovadas por
> humano, implementação por agentes com gates de build/teste, revisão independente e QA em browser
> real.

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
dentro do container, então basta apontar o `psql` para o arquivo novo lá dentro, por exemplo:

```sh
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f /docker-entrypoint-initdb.d/0002_exemplo.sql'
```

(as variáveis `$POSTGRES_USER`/`$POSTGRES_DB` precisam ser expandidas **dentro** do container —
onde o compose as injeta via `environment:` — por isso o `sh -c` envolvendo o `psql`; expandi-las
no shell do host não funciona, pois o host não tem essas variáveis.)

**Resetar o banco do zero** (descarta os dados do container local — sempre sintéticos, nunca dado
real):

```sh
docker compose down -v
```

### Consulta de similaridade (inspeção manual)

Depois de popular o banco (`dotnet run --project src/Prumo.Seed` — comando e providers documentados
no fechamento do M1), a similaridade por cosseno usada pelo M1 pode ser inspecionada direto via
`psql`, com o operador `<=>` do pgvector ordenando por distância (menor = mais próximo):

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
