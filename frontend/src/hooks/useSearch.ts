import { useCallback, useEffect, useRef, useState } from 'react'

import { fetchSearch, type SearchError, type SearchParams, type SearchResponse } from '../api/search'

export type UseSearchState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'success'; readonly data: SearchResponse }
  | { readonly status: 'error'; readonly error: SearchError }

export interface UseSearchResult {
  readonly state: UseSearchState
  readonly search: (params: SearchParams) => void
}

/**
 * Orquestra `fetchSearch` com cancelamento e descarte de resposta obsoleta (spec.md
 * "Concorrência e Idempotência": "duas buscas rápidas em sequência nunca podem pintar a tela com
 * o resultado da primeira"; design.md §7 "Cancelamento e resposta obsoleta").
 *
 * Duas defesas independentes, deliberadamente redundantes:
 * - `AbortController.abort()` cancela de fato a requisição HTTP anterior — a rede não continua
 *   trabalhando para um resultado que ninguém vai mostrar;
 * - um contador de geração (`latestRequestIdRef`) decide, de forma determinística, se uma resposta
 *   que chega ainda é a mais recente. Esta segunda defesa NÃO depende do abort ter interrompido a
 *   promise a tempo — numa corrida real de rede (ou num teste que resolve a primeira promise DEPOIS
 *   da segunda), a resposta antiga pode terminar de qualquer forma, e mesmo assim precisa ser
 *   ignorada.
 */
export function useSearch(): UseSearchResult {
  const [state, setState] = useState<UseSearchState>({ status: 'idle' })
  const abortControllerRef = useRef<AbortController | null>(null)
  const latestRequestIdRef = useRef(0)

  const search = useCallback((params: SearchParams) => {
    // Aborta a busca anterior (se houver) ANTES de criar o novo controller — nunca duas buscas em
    // voo com o controller "atual" apontando para a mais velha.
    abortControllerRef.current?.abort()

    const controller = new AbortController()
    abortControllerRef.current = controller

    const requestId = latestRequestIdRef.current + 1
    latestRequestIdRef.current = requestId

    setState({ status: 'loading' })

    void fetchSearch(params, controller.signal).then((result) => {
      // Resposta obsoleta: uma busca mais nova já começou depois desta ter sido disparada.
      // Descartada incondicionalmente — mesmo que esta promise resolva DEPOIS da mais nova.
      if (latestRequestIdRef.current !== requestId) {
        return
      }

      setState(result.ok ? { status: 'success', data: result.data } : { status: 'error', error: result.error })
    })
  }, [])

  // Desmontagem do componente: cancela qualquer busca ainda em voo, mesmo padrão `cancelled` de
  // App.tsx (M0), aqui com AbortController de verdade em vez de uma flag booleana.
  useEffect(() => {
    return () => {
      abortControllerRef.current?.abort()
    }
  }, [])

  return { state, search }
}
