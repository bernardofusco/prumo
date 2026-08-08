-- 0002_specialties_and_professionals.sql
-- Schema de domínio do M1 (MET-478): especialidades e profissionais. A integridade é constraint
-- de banco — PK, UNIQUE, FK e CHECK nascem aqui; validação de aplicação é UX, não defesa
-- (project/development-rules.md). Migrations são forward-only: este arquivo nunca é editado depois
-- de aplicado — correções futuras entram em um novo arquivo (0003_*.sql, ...).
--
-- Depende de 0001_extensions.sql (cube/earthdistance, usadas pelo índice geográfico abaixo).
-- A coluna vetorial (embedding) e sua procedência NÃO entram aqui — são a próxima migration
-- (0003_professional_embeddings.sql, T2). Este arquivo é domínio puro, independente da decisão do
-- provedor de embeddings (ADR-002).

CREATE TABLE IF NOT EXISTS specialties (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slug       text NOT NULL UNIQUE,
    name       text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT specialties_slug_format CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$')
);

CREATE TABLE IF NOT EXISTS professionals (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slug                 text NOT NULL UNIQUE,
    full_name            text NOT NULL,
    service_description  text NOT NULL,
    specialty_id         bigint NOT NULL REFERENCES specialties (id) ON DELETE RESTRICT,
    city                 text NOT NULL,
    state                text NOT NULL,
    latitude             double precision NOT NULL,
    longitude            double precision NOT NULL,
    service_radius_km    integer NOT NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),

    -- slug: mesma convenção de specialties (chave de negócio, usada pelo upsert do seed via
    -- ON CONFLICT e, futuramente, em URL — MET-479).
    CONSTRAINT professionals_slug_format        CHECK (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'),
    CONSTRAINT professionals_full_name_present  CHECK (length(btrim(full_name)) >= 3),
    -- Descrição vazia ou curta demais produziria embedding sem sinal e um buraco silencioso na
    -- régua do M1 (design.md §2.1). O número é convenção do corpus, não do domínio.
    CONSTRAINT professionals_description_length CHECK (length(btrim(service_description)) >= 40),
    -- Duas letras maiúsculas: o schema não engessa "MG/RJ/SP" — isso é característica do corpus
    -- v1, não do domínio (design.md §2.1).
    CONSTRAINT professionals_state_format       CHECK (state ~ '^[A-Z]{2}$'),
    CONSTRAINT professionals_latitude_range     CHECK (latitude  BETWEEN  -90 AND  90),
    CONSTRAINT professionals_longitude_range    CHECK (longitude BETWEEN -180 AND 180),
    CONSTRAINT professionals_radius_range       CHECK (service_radius_km BETWEEN 1 AND 200)
);

CREATE INDEX IF NOT EXISTS professionals_specialty_id_idx ON professionals (specialty_id);

-- Índice de expressão GiST para o filtro por raio do M1 (design.md §2.1, D5 da spec MET-478):
-- ll_to_earth(lat, lon) é o padrão documentado da extensão earthdistance (habilitada em
-- 0001_extensions.sql) para tornar earth_box/earth_distance sargáveis. É um índice EXATO — acelera
-- sem mudar resultado. Não confundir com um índice ANN: a coluna vetorial (embedding, 0003)
-- permanece deliberadamente SEM índice (ver comentário lá e SchemaIndexesTests aqui).
CREATE INDEX IF NOT EXISTS professionals_earth_idx
    ON professionals USING gist (ll_to_earth(latitude, longitude));
