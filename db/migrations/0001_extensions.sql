-- 0001_extensions.sql
-- Habilita as extensões usadas pelo Prumo: busca vetorial (pgvector) e filtro por raio
-- (earthdistance, que depende de cube). Migrations são forward-only: esta migration nunca é
-- editada depois de aplicada — correções futuras entram em um novo arquivo (0002_*.sql, ...).
--
-- Ordem importa: earthdistance depende de cube.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS cube;          -- pré-requisito de earthdistance
CREATE EXTENSION IF NOT EXISTS earthdistance;
