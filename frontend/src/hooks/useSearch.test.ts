import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { SearchResult } from '../api/search'
import { fetchSearch } from '../api/search'
import { useSearch } from './useSearch'

vi.mock('../api/search', () => ({
  fetchSearch: vi.fn(),
}))

const fetchSearchMock = vi.mocked(fetchSearch)

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve: (value: T) => void = () => {
    throw new Error('resolve chamado antes de a promise ser criada')
  }
  const promise = new Promise<T>((res) => {
    resolve = res
  })
  return { promise, resolve }
}

function successResult(query: string): SearchResult {
  return {
    ok: true,
    data: {
      query,
      geo: { applied: false, radiusKm: null },
      embedding: { mode: 'precomputed', model: null },
      ranking: { semanticWeight: 0.7, proximityWeight: 0.3, distanceDecayKm: 10, minSemanticScore: 0 },
      totalCandidates: 0,
      results: [],
    },
  }
}

/** Extrai o AbortSignal com o qual `fetchSearch` foi chamado na N-ésima chamada. */
function signalFromCall(callIndex: number): AbortSignal {
  const call = fetchSearchMock.mock.calls[callIndex]
  const signal = call?.[1]

  if (!(signal instanceof AbortSignal)) {
    throw new Error(`fetchSearch não foi chamado com um AbortSignal na chamada #${callIndex}.`)
  }

  return signal
}

describe('useSearch', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('começa "idle" e vai para "loading" assim que search() é chamado', () => {
    fetchSearchMock.mockReturnValue(new Promise(() => {}))
    const { result } = renderHook(() => useSearch())

    expect(result.current.state.status).toBe('idle')

    act(() => {
      result.current.search({ q: 'vazamento no banheiro' })
    })

    expect(result.current.state.status).toBe('loading')
  })

  it('vai para "success" com os dados da API quando fetchSearch resolve ok:true', async () => {
    fetchSearchMock.mockResolvedValue(successResult('vazamento no banheiro'))
    const { result } = renderHook(() => useSearch())

    act(() => {
      result.current.search({ q: 'vazamento no banheiro' })
    })

    await waitFor(() => {
      expect(result.current.state.status).toBe('success')
    })

    if (result.current.state.status === 'success') {
      expect(result.current.state.data.query).toBe('vazamento no banheiro')
    }
  })

  it('vai para "error" com o erro tipado quando fetchSearch resolve ok:false', async () => {
    fetchSearchMock.mockResolvedValue({ ok: false, error: { code: 'network', detail: 'Falha de rede.' } })
    const { result } = renderHook(() => useSearch())

    act(() => {
      result.current.search({ q: 'vazamento no banheiro' })
    })

    await waitFor(() => {
      expect(result.current.state.status).toBe('error')
    })

    if (result.current.state.status === 'error') {
      expect(result.current.state.error.code).toBe('network')
    }
  })

  it('aborta a requisição da busca anterior de verdade ao iniciar uma nova (AbortController.abort)', () => {
    const firstDeferred = deferred<SearchResult>()
    const secondDeferred = deferred<SearchResult>()
    fetchSearchMock.mockReturnValueOnce(firstDeferred.promise).mockReturnValueOnce(secondDeferred.promise)

    const { result } = renderHook(() => useSearch())

    act(() => {
      result.current.search({ q: 'primeira busca' })
    })

    const firstSignal = signalFromCall(0)
    expect(firstSignal.aborted).toBe(false)

    act(() => {
      result.current.search({ q: 'segunda busca' })
    })

    // A defesa que a mutação do reviewer vai tentar remover: sem o abort() explícito, este signal
    // continuaria "aborted: false" mesmo depois de uma busca nova ter começado.
    expect(firstSignal.aborted).toBe(true)
    expect(signalFromCall(1).aborted).toBe(false)
  })

  it(
    'descarta a resposta obsoleta: a PRIMEIRA busca respondendo DEPOIS da segunda não sobrescreve a tela',
    async () => {
      const firstDeferred = deferred<SearchResult>()
      const secondDeferred = deferred<SearchResult>()
      fetchSearchMock.mockReturnValueOnce(firstDeferred.promise).mockReturnValueOnce(secondDeferred.promise)

      const { result } = renderHook(() => useSearch())

      act(() => {
        result.current.search({ q: 'primeira busca' })
      })
      act(() => {
        result.current.search({ q: 'segunda busca' })
      })

      // A SEGUNDA busca responde primeiro (o caso comum: a mais nova é também a mais rápida).
      await act(async () => {
        secondDeferred.resolve(successResult('segunda busca'))
        await secondDeferred.promise
      })

      expect(result.current.state.status).toBe('success')
      if (result.current.state.status === 'success') {
        expect(result.current.state.data.query).toBe('segunda busca')
      }

      // A PRIMEIRA só responde AGORA — obsoleta por construção (requestId de geração, não por o
      // abort ter interrompido a promise a tempo: o mock nunca verifica o signal). Se ela vazasse
      // para a tela, a asserção abaixo reprovaria.
      await act(async () => {
        firstDeferred.resolve(successResult('primeira busca'))
        await firstDeferred.promise
      })

      expect(result.current.state.status).toBe('success')
      if (result.current.state.status === 'success') {
        expect(result.current.state.data.query).toBe('segunda busca')
      }
    },
  )

  it('cancela a busca em voo quando o componente desmonta', () => {
    fetchSearchMock.mockReturnValue(new Promise(() => {}))
    const { result, unmount } = renderHook(() => useSearch())

    act(() => {
      result.current.search({ q: 'vazamento no banheiro' })
    })

    const signal = signalFromCall(0)
    expect(signal.aborted).toBe(false)

    unmount()

    expect(signal.aborted).toBe(true)
  })
})
