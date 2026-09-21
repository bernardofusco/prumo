-- 0005_agenda_and_reservations.sql
-- Agenda e reservas do M2 (MET-480): duas tabelas novas e a defesa oficial contra a corrida entre
-- clientes — EXCLUDE USING gist sobre o intervalo, não UNIQUE de slot (spec.md D4: UNIQUE em
-- reservations.slot_id faria a corrida estourar 23505 antes do 23P01 e roubaria o protagonismo da
-- constraint de exclusão no caminho oficial). Migrations são forward-only: este arquivo nunca é
-- editado depois de aplicado — correções futuras entram num novo arquivo (0006_*.sql, ...).
-- 0001-0004 permanecem intocados.
--
-- Depende de 0002_specialties_and_professionals.sql (tabela professionals, referenciada pelas FKs
-- abaixo).

-- btree_gist estende os métodos de acesso GiST do Postgres com suporte a operadores de IGUALDADE
-- (=) sobre tipos escalares comuns (aqui, bigint) — por padrão, GiST só sabe indexar tipos com
-- operadores "geométricos" como range/&&. As EXCLUDE abaixo misturam professional_id WITH = (
-- escalar) com period WITH && (range) no MESMO índice GiST; sem esta extensão, o
-- CREATE TABLE ... EXCLUDE falharia com "data type bigint has no default operator class for
-- access method gist". Sem btree_gist a tese do case (integridade por constraint de exclusão, não
-- por verificação em código) não existe.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE IF NOT EXISTS availability_slots (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    professional_id bigint NOT NULL REFERENCES professionals (id) ON DELETE RESTRICT,
    period          tstzrange NOT NULL,
    version         integer NOT NULL DEFAULT 0,
    source          text NOT NULL DEFAULT 'manual',
    created_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT availability_slots_period_not_empty CHECK (NOT isempty(period)),
    CONSTRAINT availability_slots_source_known     CHECK (source IN ('seed', 'manual')),
    CONSTRAINT availability_slots_version_nonneg   CHECK (version >= 0),
    -- Defesa de integridade da AGENDA do profissional (não é a régua de carga do M2, que recai
    -- sobre reservations abaixo): o profissional não publica duas janelas sobrepostas.
    CONSTRAINT availability_slots_no_overlap
        EXCLUDE USING gist (professional_id WITH =, period WITH &&)
);

CREATE TABLE IF NOT EXISTS reservations (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slot_id         bigint NOT NULL REFERENCES availability_slots (id) ON DELETE RESTRICT,
    professional_id bigint NOT NULL REFERENCES professionals (id) ON DELETE RESTRICT,
    -- Cópia do period do slot: a EXCLUDE oficial abaixo julga o INTERVALO, não o id do slot
    -- (spec.md D4). slot_id é rastreabilidade (qual janela o cliente clicou) e âncora da UNIQUE de
    -- idempotência logo abaixo.
    period          tstzrange NOT NULL,
    client_key      uuid NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT reservations_period_not_empty CHECK (NOT isempty(period)),
    -- Idempotência do MESMO cliente no MESMO slot — não é a defesa de concorrência. Note a
    -- ausência deliberada de UNIQUE (slot_id) sozinho: ver spec.md D4.
    CONSTRAINT reservations_one_per_client_slot UNIQUE (client_key, slot_id),
    -- A DEFESA OFICIAL do M2 (spec.md D1): sob N tentativas simultâneas no mesmo intervalo do
    -- mesmo profissional, o banco admite exatamente uma linha; as demais recebem SqlState 23P01
    -- (exclusion_violation), traduzido pela aplicação (T4) em 409 slot_conflict.
    CONSTRAINT reservations_no_overlap
        EXCLUDE USING gist (professional_id WITH =, period WITH &&)
);

CREATE INDEX IF NOT EXISTS availability_slots_professional_id_idx
    ON availability_slots (professional_id);
CREATE INDEX IF NOT EXISTS reservations_slot_id_idx
    ON reservations (slot_id);
