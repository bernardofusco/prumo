import type { SearchRankingInfo, SearchResultItem } from '../api/search'
import { formatDistanceKm, formatScore } from '../lib/format'

export interface ResultCardProps {
  readonly result: SearchResultItem
  readonly ranking: SearchRankingInfo
}

/**
 * Renderiza um resultado — e SÓ renderiza (design.md §7: "Nenhuma aritmética de ranking";
 * spec.md "Contrato API ↔ Frontend": "a API explica, o React renderiza"). Todo número exibido
 * (`distanceKm`, `score`, `factors.*`, `ranking.*Weight`) vem pronto da resposta; este componente
 * só formata (`lib/format.ts`) e concatena texto — nunca soma, multiplica ou reordena.
 *
 * A decomposição muda de forma conforme `factors.proximity` (BSC-10):
 * - COM localização: mostra "fator × peso = contribuição" para semântica e proximidade — os três
 *   números batem entre si porque a API garante isso (D1 da spec).
 * - SEM localização: mostra só a relevância, sem multiplicar por `ranking.semanticWeight`. É
 *   deliberado (nota da task T9): sem localização a API usa peso efetivo 1 para a semântica, mas
 *   `ranking.semanticWeight` continua reportando 0.7 (o peso configurado, não o aplicado nesta
 *   busca) — rotular como "0,71 × 0,70 = 0,71" seria uma conta que não fecha, exibida na tela.
 */
export function ResultCard({ result, ranking }: ResultCardProps) {
  const { fullName, specialty, city, state, serviceDescription, distanceKm, score, factors } = result

  return (
    <article className="result-card">
      <h2 className="result-card__name">{fullName}</h2>
      <p className="result-card__meta">
        {specialty} · {city}, {state}
      </p>
      <p className="result-card__description">{serviceDescription}</p>

      <dl className="result-card__score">
        {distanceKm !== null && (
          <div className="result-card__score-row">
            <dt>Distância</dt>
            <dd>{formatDistanceKm(distanceKm)} km</dd>
          </div>
        )}

        <div className="result-card__score-row">
          <dt>Score</dt>
          <dd>{formatScore(score)}</dd>
        </div>

        {factors.proximity !== null && factors.proximityContribution !== null ? (
          <>
            <div className="result-card__score-row">
              <dt>Relevância</dt>
              <dd>
                {formatScore(factors.semantic)} × peso {formatScore(ranking.semanticWeight)} ={' '}
                {formatScore(factors.semanticContribution)}
              </dd>
            </div>
            <div className="result-card__score-row">
              <dt>Proximidade</dt>
              <dd>
                {formatScore(factors.proximity)} × peso {formatScore(ranking.proximityWeight)} ={' '}
                {formatScore(factors.proximityContribution)}
              </dd>
            </div>
          </>
        ) : (
          <div className="result-card__score-row">
            <dt>Relevância</dt>
            <dd>{formatScore(factors.semanticContribution)} (sem localização, o score considera só a relevância)</dd>
          </div>
        )}
      </dl>
    </article>
  )
}
