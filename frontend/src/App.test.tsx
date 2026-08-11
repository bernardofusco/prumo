import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { SearchOptionsResult, SearchResponse, SearchResult } from './api/search'
import { fetchSearch, fetchSearchOptions } from './api/search'
import App from './App'

vi.mock('./api/search', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api/search')>()
  return {
    ...actual,
    fetchSearch: vi.fn(),
    fetchSearchOptions: vi.fn(),
  }
})

vi.mock('./api/health', () => ({
  fetchHealth: vi.fn().mockResolvedValue('online'),
}))

const fetchSearchMock = vi.mocked(fetchSearch)
const fetchSearchOptionsMock = vi.mocked(fetchSearchOptions)

const RANKING = { semanticWeight: 0.7, proximityWeight: 0.3, distanceDecayKm: 10, minSemanticScore: 0 }

const QUERY_LABEL = 'O que você precisa?'

function optionsResult(): SearchOptionsResult {
  return {
    ok: true,
    data: {
      embeddingMode: 'precomputed',
      defaultResultLimit: 10,
      exampleQueries: ['vazamento no banheiro'],
      cities: [{ name: 'Belo Horizonte', state: 'MG', latitude: -19.9245, longitude: -43.9352 }],
    },
  }
}

function successResponse(overrides: Partial<SearchResponse> = {}): SearchResult {
  return {
    ok: true,
    data: {
      query: 'vazamento no banheiro',
      geo: { applied: true, radiusKm: null },
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
    },
  }
}

function submitQuery(query: string) {
  fireEvent.change(screen.getByLabelText(QUERY_LABEL), { target: { value: query } })
  fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))
}

describe('App — tela de busca', () => {
  beforeEach(() => {
    fetchSearchOptionsMock.mockResolvedValue(optionsResult())
  })

  afterEach(() => {
    // `clearAllMocks` (não `resetAllMocks`): limpa histórico de chamadas mas preserva a
    // implementação padrão de `fetchHealth` (definida uma vez no factory de `vi.mock` acima) —
    // resetar tudo apagaria essa implementação após o primeiro teste.
    vi.clearAllMocks()
  })

  it('a11y básica: <form role="search">, campo com label real e região de resultados aria-live', async () => {
    fetchSearchOptionsMock.mockResolvedValue(optionsResult())
    fetchSearchMock.mockResolvedValue(successResponse())
    render(<App />)

    expect(screen.getByRole('search')).toBeDefined()
    expect(screen.getByLabelText(QUERY_LABEL)).toBeDefined()

    submitQuery('vazamento no banheiro')

    await waitFor(() => {
      const region = screen.getByRole('region', { name: 'Resultados da busca' })
      expect(region.getAttribute('aria-live')).toBe('polite')
    })
  })

  it('a região de resultados é PERSISTENTE — existe desde o carregamento, antes de qualquer busca', () => {
    render(<App />)

    // Sem esperar por nada: a região precisa estar no DOM já no primeiro render (idle), não só
    // depois que o conteúdo aparece — leitor de tela não anuncia com confiança uma região
    // aria-live que nasce no DOM já com texto dentro (achado do Reviewer).
    const region = screen.getByRole('region', { name: 'Resultados da busca' })
    expect(region.getAttribute('aria-live')).toBe('polite')
  })

  it('preserva o StatusIndicator do M0 no rodapé', async () => {
    render(<App />)

    await waitFor(() => {
      expect(screen.getByText('API online')).toBeDefined()
    })
  })

  it('menos de 2 caracteres: erro do campo tratado no cliente, sem chamar a API', () => {
    render(<App />)

    fireEvent.change(screen.getByLabelText(QUERY_LABEL), { target: { value: 'a' } })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(screen.getByRole('alert').textContent).toContain('Digite ao menos 2 caracteres')
    expect(fetchSearchMock).not.toHaveBeenCalled()
  })

  it('estado carregando: mostra "Buscando…" e desabilita o botão até a resposta chegar', async () => {
    let resolveFetch: (value: SearchResult) => void = () => {}
    fetchSearchMock.mockReturnValue(
      new Promise((resolve) => {
        resolveFetch = resolve
      }),
    )
    render(<App />)

    submitQuery('vazamento no banheiro')

    expect(screen.getByText('Buscando…', { selector: 'p[role="status"]' })).toBeDefined()
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Buscando…' }).disabled).toBe(true)

    act(() => {
      resolveFetch(successResponse())
    })

    await waitFor(() => {
      expect(screen.getByText('1 de 1 profissional(is) encontrados.')).toBeDefined()
    })
  })

  it('400 (invalid_request) da API vira erro do campo com o texto que a própria API devolveu', async () => {
    fetchSearchMock.mockResolvedValue({
      ok: false,
      error: { code: 'invalid_request', detail: 'A consulta não pode ter mais de 200 caracteres.' },
    })
    render(<App />)

    submitQuery('ok')

    await waitFor(() => {
      expect(screen.getByRole('alert').textContent).toBe('A consulta não pode ter mais de 200 caracteres.')
    })
  })

  it('lista vazia por RAIO (totalCandidates: 0) — distinta de "ninguém relevante"', async () => {
    fetchSearchMock.mockResolvedValue(successResponse({ totalCandidates: 0, results: [] }))
    render(<App />)

    submitQuery('costura de roupa de festa')

    await waitFor(() => {
      expect(screen.getByText(/Nenhum profissional atende a sua região/)).toBeDefined()
    })
  })

  it('lista vazia por CORTE (totalCandidates > 0, results: []) — distinta de "ninguém no raio"', async () => {
    fetchSearchMock.mockResolvedValue(successResponse({ totalCandidates: 5, results: [] }))
    render(<App />)

    submitQuery('vazamento no banheiro')

    await waitFor(() => {
      expect(screen.getByText(/Nenhum profissional relevante o bastante/)).toBeDefined()
      expect(screen.queryByText(/Nenhum profissional atende a sua região/)).toBeNull()
    })
  })

  it('422 (embedding_unavailable): bloco explicativo com consultas de demonstração clicáveis', async () => {
    fetchSearchMock.mockResolvedValue({
      ok: false,
      error: {
        code: 'embedding_unavailable',
        detail: 'Esta consulta não tem vetor pré-computado.',
        exampleQueries: ['meu chuveiro não esquenta'],
      },
    })
    render(<App />)

    submitQuery('meu portão não abre')

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'meu chuveiro não esquenta' })).toBeDefined()
    })

    fireEvent.click(screen.getByRole('button', { name: 'meu chuveiro não esquenta' }))

    expect(screen.getByLabelText<HTMLInputElement>(QUERY_LABEL).value).toBe('meu chuveiro não esquenta')
    // Clicar preenche o campo (spec.md J4) — NÃO dispara uma segunda busca sozinho.
    expect(fetchSearchMock).toHaveBeenCalledTimes(1)
  })

  it('422 com exampleQueries VAZIA (artefato de vetores ainda não existe) não quebra a tela', async () => {
    fetchSearchMock.mockResolvedValue({
      ok: false,
      error: { code: 'embedding_unavailable', detail: 'Esta consulta não tem vetor pré-computado.', exampleQueries: [] },
    })
    render(<App />)

    submitQuery('meu portão não abre')

    await waitFor(() => {
      expect(
        screen.getByText('Nenhuma consulta de demonstração está disponível nesta instância ainda.'),
      ).toBeDefined()
    })
  })

  it('502 (embedding_provider_error): aviso de erro com o detalhe devolvido pela API, sem segredo', async () => {
    fetchSearchMock.mockResolvedValue({
      ok: false,
      error: { code: 'embedding_provider_error', detail: 'O provedor de embeddings respondeu com erro (status 500).' },
    })
    render(<App />)

    submitQuery('vazamento no banheiro')

    // Sem role próprio (`announce={false}`, Notice.tsx): já está dentro da região aria-live
    // persistente de App.tsx — um role aninhado duplicaria o anúncio (achado do Reviewer).
    await waitFor(() => {
      expect(screen.getByText('O provedor de embeddings respondeu com erro (status 500).')).toBeDefined()
    })
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('falha de rede: aviso de erro genérico', async () => {
    fetchSearchMock.mockResolvedValue({
      ok: false,
      error: { code: 'network', detail: 'Falha de rede ao buscar profissionais.' },
    })
    render(<App />)

    submitQuery('vazamento no banheiro')

    await waitFor(() => {
      expect(screen.getByText('Falha de rede ao buscar profissionais.')).toBeDefined()
    })
  })

  it('mode: degraded — aviso visível junto dos resultados', async () => {
    fetchSearchMock.mockResolvedValue(
      successResponse({ embedding: { mode: 'degraded', model: 'hashing:v1@1024' } }),
    )
    render(<App />)

    submitQuery('vazamento no banheiro')

    await waitFor(() => {
      expect(screen.getByText(/Modo degradado/)).toBeDefined()
    })
  })

  it('falha ao carregar /options: avisa, mas a busca por texto continua funcionando', async () => {
    fetchSearchOptionsMock.mockResolvedValue({
      ok: false,
      error: { code: 'network', detail: 'Não foi possível carregar as opções de busca.' },
    })
    fetchSearchMock.mockResolvedValue(successResponse())
    render(<App />)

    await waitFor(() => {
      expect(screen.getByText(/Não foi possível carregar as cidades/)).toBeDefined()
    })

    submitQuery('vazamento no banheiro')

    await waitFor(() => {
      expect(screen.getByText('1 de 1 profissional(is) encontrados.')).toBeDefined()
    })
  })
})
