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

## Estrutura (alvo)

```
prumo/
├── Prumo.sln
├── src/Prumo.Api/          # API por contexto: Busca, Agenda, Profissionais
├── tests/Prumo.Api.Tests/  # xUnit; Category=Integration usa Testcontainers
├── frontend/               # React + TypeScript (Vite)
├── db/migrations/          # SQL forward-only — as constraints são parte da história
├── compose.yaml            # Postgres + pgvector para dev local
└── .github/workflows/      # CI
```

> **Estado atual:** repositório recém-criado — o scaffold acima é a entrega do primeiro milestone
> (M0). Este projeto é desenvolvido com o harness de agentes
> [`main-brain`](https://github.com/bernardofusco) (adapter `project-prumo`): specs aprovadas por
> humano, implementação por agentes com gates de build/teste, revisão independente e QA em browser
> real.

## Como rodar

Pré-requisito: Docker (para o banco). .NET e Node só são necessários quando `src/` e `frontend/`
tiverem código executável.

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
docker compose exec db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -f /docker-entrypoint-initdb.d/0002_exemplo.sql
```

**Resetar o banco do zero** (descarta os dados do container local — sempre sintéticos, nunca dado
real):

```sh
docker compose down -v
```

### API e frontend

```sh
dotnet run --project src/Prumo.Api      # API em porta fixa (ver Properties/launchSettings.json)
npm --prefix frontend run dev           # Vite, com proxy de /api para a API
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
