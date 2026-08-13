# `eval/concurrency-ledger.md` — a régua do M2 (agendamento sob concorrência)

> Gerado e atualizado por `tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs` (MET-480 T15) — os números abaixo são a saída REAL da última execução verde deste teste, não texto escrito à mão. Rodar o teste de novo regenera este arquivo por inteiro.

Esta é a **segunda régua nomeada do case**, ao lado do golden set do M1 (`eval/README.md`, `eval/golden-set.json`). Mede uma coisa só: sob N tentativas simultâneas de reserva no MESMO horário do MESMO profissional, com N clientes distintos, a defesa admite EXATAMENTE uma. **Mudar N, o critério "exatamente 1 sucesso" (C1), "N−1 recusas 409/`slot_conflict`" (C2) ou "zero qualquer outro status" (C3) é ADR + decisão do dono** — nunca edição silenciosa desta tabela nem de `Prumo.Api.Agenda.Scheduling.LoadVerdict`.

`n` = `LoadVerdict.N` = **20** `POST /api/reservations` simultâneos (HTTP real, via `WebApplicationFactory`) no mesmo `slotId`, com 20 `clientKey` distintos (UUIDs sintéticos, nenhum gravado neste arquivo) — repetido uma vez por defesa, cada rodada com um profissional/slot PRÓPRIOS (estado isolado, nunca reaproveitado entre defesas). `durationMs` é a parede de relógio do lote inteiro — **informativo, não é régua** (ADR-007: a tese do M2 é integridade sob corrida, não RPS).

## As três defesas, medidas

| defense | n | successes | conflicts | other | durationMs |
|---|---:|---:|---:|---:|---:|
| `exclusion` | 20 | 1 | 19 | 0 | 18052 |
| `pessimistic` | 20 | 1 | 19 | 0 | 74 |
| `optimistic` | 20 | 1 | 19 | 0 | 62 |

`other` é 0 nas três linhas — nenhuma recusa saiu como `503`/`500`/`422`/`200` replay/timeout/resposta ausente. Este é exatamente o buraco que a spec.md "Contexto" nomeia ("um teste de carga que só contasse sucessos ainda passaria") — a linha `exclusion` só fecha `other = 0` porque o tradutor de conflito (T4) e a tradução do deadlock `40P01` (`project/adr/ADR-008-deadlock-da-exclusao-e-conflito-de-negocio.md`) estão no lugar: sem a ADR-008, a mesma corrida mediu `1×201 + 19×503` contra container frio.

## Ablação: a EXCLUDE removida de propósito

A issue MET-480 pede literalmente: "repetir com a defesa de aplicação removida de propósito — só a constraint sobrevive a código que 'esquece de checar'". **Esta seção foi AUTOMATIZADA por este mesmo teste** (não é a medição de um reviewer copiada à mão): num Postgres descartável e ISOLADO do fixture compartilhado da suíte de integração (mesma imagem/migrations, container próprio, nunca `db/migrations/0005_agenda_and_reservations.sql` editado), a constraint `EXCLUDE reservations_no_overlap` é removida por SQL cru logo após o boot; o container inteiro é descartado ao final da execução — não há "restaurar a constraint" porque nada do que ele contém sobrevive além da chamada.

Cada variante roda 3 rodadas de N=20 tentativas concorrentes (chamando a defesa direto, sem HTTP — a mesma técnica que `ExclusionDefenseTests`/`PessimisticDefenseTests`/`OptimisticDefenseTests` já usam para a régua por-defesa), num slot novo por rodada. A coluna "linhas persistidas por rodada" é a contagem REAL no banco (não o que cada defesa autorrelatou) — é essa contagem que prova "colapsa" ou "sobrevive". Esta tabela **não é** a régua C1-C4 (AGN-11) e não substitui a tabela acima — é evidência adicional para a tese do case.

| variant | n | rounds | linhas persistidas por rodada | colapsa? |
|---|---:|---:|---|---|
| `exclusion (sem EXCLUDE)` | 20 | 3 | 20, 20, 20 | sim |
| `pessimistic (sem EXCLUDE)` | 20 | 3 | 1, 1, 1 | não |
| `optimistic (sem EXCLUDE)` | 20 | 3 | 1, 1, 1 | não |
| `pessimistic sem FOR UPDATE (sem EXCLUDE)` | 20 | 3 | 20, 5, 4 | sim |

**A leitura:** `exclusion` colapsa sem a constraint (ela NÃO tem defesa nenhuma em código — "insere e deixa o banco decidir" é a frase literal, e sem banco decidindo não sobra nada); `pessimistic` e `optimistic` sobrevivem sozinhas (o lock de linha e a incrementação condicional de `version` são mecanismos de APLICAÇÃO, independentes da EXCLUDE); a variante "sem `FOR UPDATE`" — uma cópia da defesa pessimista com o lock removido de propósito, só para este teste, nunca a `PessimisticDefense.cs` de produção — mostra o que acontece quando alguém esquece: sem a constraint E sem o lock, nada segura a corrida. É exatamente o ponto do case: **integridade é constraint de banco, não convenção de código** — a defesa oficial (`exclusion`) é a única das três que não sobrevive sozinha, e é isso que a torna a defesa certa para produção (ela não depende de ninguém lembrar de nada).
