import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { SearchResponse } from '../api/search'
import { ResultList } from './ResultList'

const RANKING = { semanticWeight: 0.7, proximityWeight: 0.3, distanceDecayKm: 10, minSemanticScore: 0 }

function baseResponse(overrides: Partial<SearchResponse> = {}): SearchResponse {
  return {
    query: 'vazamento no banheiro',
    geo: { applied: true, radiusKm: 25 },
    embedding: { mode: 'precomputed', model: 'openai:text-embedding-3-small@1024' },
    ranking: RANKING,
    totalCandidates: 1,
    results: [
      {
        slug: 'ana-ribeiro-bh-01',
        fullName: 'Ana Ribeiro',
        specialty: 'Encanador',
        city: 'Belo Horizonte',
        state: 'MG',
        serviceDescription: 'Atendo emergência de goteira embaixo da pia.',
        distanceKm: 3.42,
        score: 0.8231,
        factors: { semantic: 0.7104, proximity: 0.7118, semanticContribution: 0.4973, proximityContribution: 0.2135 },
      },
    ],
    ...overrides,
  }
}

// A região `aria-live="polite"` é persistente e vive em `App.tsx` (App.test.tsx cobre isso) —
// este componente devolve só o CONTEÚDO (fragmento), de propósito: uma região viva que só nasce
// no DOM já com texto dentro não é confiavelmente anunciada por leitor de tela.
describe('ResultList', () => {
  it('renderiza a lista como <ol role="list">, um item por resultado', () => {
    render(<ResultList response={baseResponse()} />)

    const list = screen.getByRole('list')
    expect(list.tagName).toBe('OL')
    expect(screen.getAllByRole('listitem')).toHaveLength(1)
  })

  it('sem localização (geo.applied: false): mostra o aviso permanente e nenhuma distância', () => {
    render(
      <ResultList
        response={baseResponse({
          geo: { applied: false, radiusKm: null },
          results: [
            {
              slug: 'camila-correia-dcx-009',
              fullName: 'Camila Correia',
              specialty: 'Encanador',
              city: 'Duque de Caxias',
              state: 'RJ',
              serviceDescription: 'Atendo com urgência.',
              distanceKm: null,
              score: 0.39,
              factors: { semantic: 0.39, proximity: null, semanticContribution: 0.39, proximityContribution: null },
            },
          ],
        })}
      />,
    )

    expect(screen.getByText(/Sem localização informada/)).toBeDefined()
    expect(screen.queryByText(/km$/)).toBeNull()
  })

  it('mode: degraded — mostra o aviso de modo degradado', () => {
    render(<ResultList response={baseResponse({ embedding: { mode: 'degraded', model: 'hashing:v1@1024' } })} />)

    expect(screen.getByText(/Modo degradado/)).toBeDefined()
  })

  it('não mostra nenhum aviso quando há localização e o modo não é degradado', () => {
    render(<ResultList response={baseResponse()} />)

    expect(screen.queryByText(/Sem localização informada/)).toBeNull()
    expect(screen.queryByText(/Modo degradado/)).toBeNull()
  })

  it('lista vazia por RAIO (totalCandidates: 0) — mensagem distinta de "ninguém relevante"', () => {
    render(<ResultList response={baseResponse({ totalCandidates: 0, results: [] })} />)

    expect(screen.getByText(/Nenhum profissional atende a sua região/)).toBeDefined()
    expect(screen.queryByRole('list')).toBeNull()
  })

  it('lista vazia por CORTE (totalCandidates > 0, results: []) — mensagem distinta de "ninguém no raio"', () => {
    render(<ResultList response={baseResponse({ totalCandidates: 4, results: [] })} />)

    expect(screen.getByText(/Nenhum profissional relevante o bastante/)).toBeDefined()
    expect(screen.queryByText(/Nenhum profissional atende a sua região/)).toBeNull()
  })

  it('resumo mostra quantos resultados de quantos candidatos', () => {
    render(<ResultList response={baseResponse({ totalCandidates: 37 })} />)

    expect(screen.getByText('1 de 37 profissional(is) encontrados.')).toBeDefined()
  })
})
