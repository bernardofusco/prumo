import { afterEach, describe, expect, it, vi } from 'vitest'

import { fetchSearch, fetchSearchOptions } from './search'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

function problemResponse(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(
    JSON.stringify({
      title: 'Título de erro',
      detail: 'Detalhe do erro em pt-BR.',
      code,
      ...extra,
    }),
    { status, headers: { 'content-type': 'application/problem+json' } },
  )
}

const VALID_SEARCH_BODY = {
  query: 'vazamento no banheiro',
  geo: { applied: true, radiusKm: 25 },
  embedding: { mode: 'precomputed', model: 'openai:text-embedding-3-small@1024' },
  ranking: { semanticWeight: 0.7, proximityWeight: 0.3, distanceDecayKm: 10, minSemanticScore: 0 },
  totalCandidates: 1,
  results: [
    {
      slug: 'ana-ribeiro-bh-01',
      fullName: 'Ana Ribeiro',
      specialty: 'Encanador',
      city: 'Belo Horizonte',
      state: 'MG',
      serviceDescription: 'Atendo emergência de goteira.',
      distanceKm: 3.42,
      score: 0.8231,
      factors: {
        semantic: 0.7104,
        proximity: 0.7118,
        semanticContribution: 0.4973,
        proximityContribution: 0.2135,
      },
    },
  ],
}

const VALID_OPTIONS_BODY = {
  embeddingMode: 'precomputed',
  defaultResultLimit: 10,
  maxQueryLength: 200,
  exampleQueries: ['vazamento no banheiro'],
  cities: [{ name: 'Belo Horizonte', state: 'MG', latitude: -19.9245, longitude: -43.9352 }],
}

/** Extrai os query params da URL com a qual `fetch` foi chamado — evita comparar string crua. */
function calledUrlParams(fetchMock: ReturnType<typeof vi.fn>, callIndex = 0): URLSearchParams {
  const [url] = fetchMock.mock.calls[callIndex] as [string, RequestInit | undefined]
  return new URL(url, 'http://localhost').searchParams
}

describe('fetchSearch', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('monta a URL com "q" e, sem localização, NENHUM parâmetro lat/lng/radiusKm', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchSearch({ q: 'vazamento no banheiro' })

    const params = calledUrlParams(fetchMock)
    expect(params.get('q')).toBe('vazamento no banheiro')
    expect(params.has('lat')).toBe(false)
    expect(params.has('lng')).toBe(false)
    expect(params.has('radiusKm')).toBe(false)
  })

  it('descarta radiusKm quando não há localização — o backend rejeitaria essa combinação com 400', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchSearch({ q: 'vazamento no banheiro', radiusKm: 25 })

    const params = calledUrlParams(fetchMock)
    expect(params.has('radiusKm')).toBe(false)
  })

  it('descarta radiusKm quando só "lat" foi informado (sem "lng", ainda não é localização válida)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchSearch({ q: 'vazamento no banheiro', lat: -19.9245, radiusKm: 25 })

    const params = calledUrlParams(fetchMock)
    expect(params.has('lat')).toBe(false)
    expect(params.has('radiusKm')).toBe(false)
  })

  it('arredonda lat/lng para 3 casas decimais antes de virar parâmetro (minimização de dado)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchSearch({ q: 'vazamento no banheiro', lat: -19.924567, lng: -43.935187, radiusKm: 25, limit: 10 })

    const params = calledUrlParams(fetchMock)
    expect(params.get('lat')).toBe('-19.925')
    expect(params.get('lng')).toBe('-43.935')
    // Nem uma 4ª casa decimal sobrevive (mutante "mais casas" precisa reprovar aqui).
    expect(params.get('lat')?.split('.')[1]).toHaveLength(3)
    expect(params.get('lng')?.split('.')[1]).toHaveLength(3)
    expect(params.get('radiusKm')).toBe('25')
    expect(params.get('limit')).toBe('10')
  })

  it('repassa o AbortSignal ao fetch', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await fetchSearch({ q: 'vazamento no banheiro' }, controller.signal)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })

  it('retorna ok:true com os dados quando a resposta é 200 no formato esperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(VALID_SEARCH_BODY)))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.results).toHaveLength(1)
      expect(result.data.results[0]?.distanceKm).toBe(3.42)
      expect(result.data.results[0]?.factors.proximity).toBe(0.7118)
    }
  })

  it('sem localização: distanceKm e factors.proximity chegam null e são preservados como null', async () => {
    const bodyWithoutLocation = {
      ...VALID_SEARCH_BODY,
      geo: { applied: false, radiusKm: null },
      results: [
        {
          ...VALID_SEARCH_BODY.results[0],
          distanceKm: null,
          factors: { ...VALID_SEARCH_BODY.results[0]?.factors, proximity: null, proximityContribution: null },
        },
      ],
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(bodyWithoutLocation)))

    const result = await fetchSearch({ q: 'preciso pintar a sala' })

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.geo.applied).toBe(false)
      expect(result.data.results[0]?.distanceKm).toBeNull()
      expect(result.data.results[0]?.factors.proximity).toBeNull()
      expect(result.data.results[0]?.factors.proximityContribution).toBeNull()
    }
  })

  it('retorna code "invalid_request" tipado para 400, sem lançar exceção', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(400, 'invalid_request')))

    const result = await fetchSearch({ q: 'x' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('invalid_request')
      expect(result.error.detail).toBe('Detalhe do erro em pt-BR.')
    }
  })

  it('retorna code "embedding_unavailable" com exampleQueries para 422', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        problemResponse(422, 'embedding_unavailable', {
          exampleQueries: ['vazamento no banheiro', 'meu chuveiro não esquenta'],
        }),
      ),
    )

    const result = await fetchSearch({ q: 'meu portão não abre' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('embedding_unavailable')
      expect(result.error.exampleQueries).toEqual(['vazamento no banheiro', 'meu chuveiro não esquenta'])
    }
  })

  it('retorna code "embedding_provider_error" para 502, sem vazar nenhum detalhe além do "detail" do corpo', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(502, 'embedding_provider_error')))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('embedding_provider_error')
    }
  })

  it('degrada para "network" quando o código de erro não é um dos três conhecidos', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(500, 'internal_server_error')))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando a resposta de erro não é JSON', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>502</html>', { status: 502 })))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando o corpo 200 vem em formato inesperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ oops: true })))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  // Validação PROFUNDA (achado do review da T8): um corpo cujas 6 chaves de topo existem mas cujo
  // conteúdo está errado tem de reprovar aqui — não na T9, desreferenciando `undefined` na tela.
  describe('validação profunda do corpo 200 (não só as 6 chaves de topo)', () => {
    it('retorna "network" quando um item de results tem um campo com o TIPO errado (slug numérico)', async () => {
      const malformed = {
        ...VALID_SEARCH_BODY,
        results: [{ ...VALID_SEARCH_BODY.results[0], slug: 123 }],
      }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })

    it('retorna "network" quando factors vem null em vez do objeto decomposto', async () => {
      const malformed = {
        ...VALID_SEARCH_BODY,
        results: [{ ...VALID_SEARCH_BODY.results[0], factors: null }],
      }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })

    it('retorna "network" quando factors.semantic tem o tipo errado (string em vez de number)', async () => {
      const malformed = {
        ...VALID_SEARCH_BODY,
        results: [
          {
            ...VALID_SEARCH_BODY.results[0],
            factors: { ...VALID_SEARCH_BODY.results[0]?.factors, semantic: 'muito alto' },
          },
        ],
      }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })

    it('retorna "network" quando ranking vem vazio (sem semanticWeight/proximityWeight/etc.)', async () => {
      const malformed = { ...VALID_SEARCH_BODY, ranking: {} }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })

    it('retorna "network" quando geo vem vazio (sem "applied")', async () => {
      const malformed = { ...VALID_SEARCH_BODY, geo: {} }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })

    it('retorna "network" quando embedding.mode está ausente', async () => {
      const malformed = { ...VALID_SEARCH_BODY, embedding: { model: null } }
      vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

      const result = await fetchSearch({ q: 'vazamento no banheiro' })

      expect(result.ok).toBe(false)
      if (!result.ok) {
        expect(result.error.code).toBe('network')
      }
    })
  })

  it('retorna "network" sem lançar exceção quando o fetch rejeita (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(fetchSearch({ q: 'vazamento no banheiro' })).resolves.toEqual({
      ok: false,
      error: { code: 'network', detail: expect.any(String) as string },
    })
  })

  it('retorna "network" sem lançar exceção quando o fetch é abortado', async () => {
    const abortError = new DOMException('The operation was aborted.', 'AbortError')
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(abortError))

    const result = await fetchSearch({ q: 'vazamento no banheiro' })

    expect(result.ok).toBe(false)
  })
})

describe('fetchSearchOptions', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('retorna ok:true com os dados quando a resposta é 200 no formato esperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(VALID_OPTIONS_BODY)))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.exampleQueries).toEqual(['vazamento no banheiro'])
      expect(result.data.cities).toHaveLength(1)
      expect(result.data.maxQueryLength).toBe(200)
    }
  })

  it('repassa o AbortSignal ao fetch', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_OPTIONS_BODY))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await fetchSearchOptions(controller.signal)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })

  it('retorna "network" sem lançar exceção quando a resposta HTTP não é ok', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando o corpo vem em formato inesperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ oops: true })))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" quando um item de cities tem o tipo errado (latitude como string)', async () => {
    const malformed = {
      ...VALID_OPTIONS_BODY,
      cities: [{ name: 'Belo Horizonte', state: 'MG', latitude: '-19.9245', longitude: -43.9352 }],
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" quando um item de exampleQueries não é string', async () => {
    const malformed = { ...VALID_OPTIONS_BODY, exampleQueries: [123] }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" quando maxQueryLength tem o tipo errado (string em vez de number)', async () => {
    const malformed = { ...VALID_OPTIONS_BODY, maxQueryLength: '200' }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

    const result = await fetchSearchOptions()

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando o fetch rejeita (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(fetchSearchOptions()).resolves.toEqual({
      ok: false,
      error: { code: 'network', detail: expect.any(String) as string },
    })
  })
})
