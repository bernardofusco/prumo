import type { SearchResponse } from '../api/search'
import { Notice } from './Notice'
import { ResultCard } from './ResultCard'

export interface ResultListProps {
  readonly response: SearchResponse
  /** Repassado a cada `ResultCard` (MET-480 T13) — ver a doc de `ResultCardProps.onViewSlots`. */
  readonly onViewSlots: (slug: string) => void
}

/**
 * O CONTEÚDO da região de resultados — não a região em si (BSC-12: "região de resultados com
 * aria-live='polite'"). A região `aria-live` é persistente e vive em `App.tsx`, montada desde o
 * carregamento inicial da tela, não só quando `response` existe (achado do Reviewer: uma região
 * `aria-live` que só é inserida no DOM já com conteúdo dentro — "nasce junto com o conteúdo" — não
 * é confiavelmente anunciada por leitor de tela; a região precisa existir ANTES do conteúdo mudar).
 * Por isso este componente devolve só o conteúdo (fragmento), sem `<section aria-live>` próprio.
 *
 * Os dois avisos que precedem a lista (`Notice`) vêm de campos que **a própria resposta** carrega
 * (`embedding.mode`, `geo.applied`) — não de uma suposição do frontend. `announce={false}` neles:
 * já estão dentro da região viva de `App.tsx`, e um `role="status"` aninhado dentro de um
 * `aria-live="polite"` externo tende a duplicar o anúncio (mesmo achado do Reviewer).
 *
 * `role="list"` explícito no `<ol>`: o `list-style: none` do CSS (App.css) faz o Safari/VoiceOver
 * descartar a semântica de lista nativa do `<ol>` — o Chromium não tem esse problema, mas a
 * mitigação é barata e não depende de qual motor o visitante usa.
 */
export function ResultList({ response, onViewSlots }: ResultListProps) {
  const { results, totalCandidates, geo, embedding, ranking } = response

  return (
    <>
      {embedding.mode === 'degraded' && (
        <Notice tone="warning" announce={false}>
          Modo degradado: esta instância roda sem embeddings semânticos reais (provedor de teste). Os
          resultados abaixo não representam a qualidade da busca do case.
        </Notice>
      )}

      {!geo.applied && (
        <Notice tone="info" announce={false}>
          Sem localização informada: os resultados estão ordenados só por relevância — a distância não
          está sendo considerada.
        </Notice>
      )}

      {results.length === 0 ? (
        <p className="result-list__empty">
          {totalCandidates === 0
            ? 'Nenhum profissional atende a sua região para esta busca. Tente aumentar o raio ou buscar sem localização.'
            : 'Nenhum profissional relevante o bastante foi encontrado para esta busca. Tente descrever o serviço de outra forma.'}
        </p>
      ) : (
        <>
          <p className="result-list__summary">
            {results.length} de {totalCandidates} profissional(is) encontrados.
          </p>
          <ol className="result-list__items" role="list">
            {results.map((result) => (
              <li key={result.slug}>
                <ResultCard result={result} ranking={ranking} onViewSlots={onViewSlots} />
              </li>
            ))}
          </ol>
        </>
      )}
    </>
  )
}
