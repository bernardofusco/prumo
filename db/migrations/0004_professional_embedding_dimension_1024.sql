-- 0004_professional_embedding_dimension_1024.sql
-- Dimensão canônica do vetor: 768 -> 1024 (MET-521). Migrations são forward-only: este arquivo nunca
-- é editado depois de aplicado — e a 0003 (que criou a coluna `vector(768)`) tampouco é tocada aqui;
-- correções futuras entram numa 0005.
--
-- Por quê: a D3 da spec MET-478 (specs/features/met-478-modelagem-e-ingestao/spec.md, no repo do
-- harness) fixava 768 "para qualquer provedor", com o racional de que um número fixo desacopla o
-- schema da decisão de embeddings (ADR-002). Essa premissa foi refutada por medição, feita contra o
-- corpus real, com 3 pares de verificação em português: `text-embedding-nomic-embed-text-v1.5`
-- (768 dims) acerta 2/3 e falha justamente o caso do DoD ("vazamento no banheiro" perde para um
-- PINTOR por 0,0002 de distância); `text-embedding-bge-m3` (1024 dims) acerta 3/3, com margem de
-- +0,1272 sobre o mesmo distrator no mesmo caso. O LM Studio ignora o parâmetro `dimensions` da
-- requisição (pedimos 768, recebemos 1024 de volta), e o bge-m3 não é treinado para truncamento —
-- ao contrário do nomic v1.5, ele não é Matryoshka — então cortar o vetor para 768 degradaria a
-- qualidade de um jeito que nenhum teste estrutural (dimensão, norma, idempotência) pegaria. A
-- dimensão canônica só desacopla o schema do provedor SE o provedor realmente escolhido a suportar; o
-- modelo que resolve pt-BR não suporta 768.
--
-- Depende de 0003_professional_embeddings.sql (coluna `embedding vector(768)` + procedência). Os
-- vetores hoje gravados neste banco foram gerados pelo provider `hashing` em 768 dimensões — mesmo
-- que fossem de um provedor real, seriam de outra dimensão e, portanto, incompatíveis com
-- `vector(1024)`. A CHECK `professionals_embedding_provenance_coherent` (0003) exige os quatro campos
-- de procedência nulos OU os quatro preenchidos: gravar só a coluna do vetor "redimensionada" sem
-- procedência coerente violaria essa constraint, e forjar um valor de procedência para um vetor que
-- não veio daquele modelo mascararia a origem real do dado. Por isso os quatro campos são zerados
-- JUNTOS abaixo, nunca só a coluna do vetor — os vetores são regeneráveis:
-- `dotnet run --project src/Prumo.Seed` os recria (ver README.md, seção "Seed").
--
-- No código, a dimensão vive em Prumo.Api.Embeddings.EmbeddingDefaults.Dimensions — os dois lados
-- (schema e código) mudam juntos, nunca um sem o outro.
--
-- Idempotente no sentido operacional (spec.md "Concorrência e Idempotência"): reaplicar este arquivo
-- num banco que já está em vector(1024) não falha — a segunda UPDATE não encontra linha nenhuma com
-- `embedding IS NOT NULL` (WHERE não casa nada) e o ALTER COLUMN TYPE para o mesmo tipo que a coluna
-- já tem é um no-op aceito pelo Postgres.

UPDATE professionals
SET embedding             = NULL,
    embedding_model       = NULL,
    embedding_source_hash = NULL,
    embedded_at           = NULL
WHERE embedding IS NOT NULL;

ALTER TABLE professionals
    ALTER COLUMN embedding TYPE vector(1024);
