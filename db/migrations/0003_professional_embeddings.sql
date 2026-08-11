-- 0003_professional_embeddings.sql
-- Coluna vetorial e procedência do embedding de professionals (MET-478, T2). Migrations são
-- forward-only: este arquivo nunca é editado depois de aplicado — correções futuras entram em um
-- novo arquivo (0004_*.sql, ...). Se a decisão do provedor de embeddings (ADR-002) exigir outra
-- dimensão, entra uma 0004 com ALTER COLUMN embedding TYPE vector(N) + re-seed (design.md §2.2).
--
-- Depende de 0001_extensions.sql (extensão vector) e 0002_specialties_and_professionals.sql (tabela
-- professionals). Dimensão 768 é decisão de projeto (D3 da spec), não consequência de fornecedor —
-- desacopla este schema do gate humano de custo externo (ADR-002/ING-15). No código, 768 vive
-- apenas em Prumo.Api.Embeddings.EmbeddingDefaults.Dimensions (T5) — este SQL é o outro único lugar
-- onde o número aparece.

ALTER TABLE professionals
    ADD COLUMN IF NOT EXISTS embedding             vector(768),
    ADD COLUMN IF NOT EXISTS embedding_model       text,
    ADD COLUMN IF NOT EXISTS embedding_source_hash text,
    ADD COLUMN IF NOT EXISTS embedded_at           timestamptz;

-- Procedência do vetor: não existe "vetor sem procedência" nem "procedência sem vetor" — os quatro
-- campos andam juntos. Forma explícita (em vez de num_nulls(...) IN (0,4)) por legibilidade para
-- quem lê o repo (design.md §2.2) — equivalente, mas mais direta de ler sem consultar a doc do
-- Postgres.
--
-- O bloco DO abaixo existe só porque o Postgres não tem `ADD CONSTRAINT IF NOT EXISTS` — ao
-- contrário de ADD COLUMN, que tem. A guarda manual em pg_constraint é o jeito de manter esta
-- migration idempotente no sentido operacional exigido por spec.md ("Concorrência e Idempotência":
-- 0002/0003 são idempotentes, IF NOT EXISTS onde a semântica permite), para reaplicação segura em
-- banco de dev já existente. A guarda é escopada por tabela (conrelid), não só por nome
-- (conname) — nome de CHECK só precisa ser único por tabela, então checar só pg_constraint.conname
-- casaria com uma constraint de mesmo nome em OUTRA tabela e pularia a criação em silêncio aqui.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'public.professionals'::regclass
          AND conname = 'professionals_embedding_provenance_coherent'
    ) THEN
        ALTER TABLE professionals
            ADD CONSTRAINT professionals_embedding_provenance_coherent CHECK (
                (embedding IS NULL     AND embedding_model IS NULL     AND embedding_source_hash IS NULL     AND embedded_at IS NULL)
             OR (embedding IS NOT NULL AND embedding_model IS NOT NULL AND embedding_source_hash IS NOT NULL AND embedded_at IS NOT NULL)
            );
    END IF;
END
$$;

-- Deliberadamente SEM índice ANN. Com ~150 linhas a varredura exata é instantânea e tem recall
-- 100%; HNSW/IVFFlat trocam recall por velocidade, e qualquer perda de recall entraria na régua
-- do M1 como ruído indistinguível de um bug de ranking. Em escala (ordem de 10^5 linhas), o
-- índice coerente com a métrica do projeto (cosseno, <=>) seria:
--   CREATE INDEX ON professionals USING hnsw (embedding vector_cosine_ops);
