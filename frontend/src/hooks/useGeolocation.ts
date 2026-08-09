import { useCallback, useState } from 'react'

/**
 * Wrapper fino sobre `navigator.geolocation.getCurrentPosition` (spec.md F1/D4, design.md §7:
 * "hooks/useGeolocation.ts wrapper fino"). Não decide nada sozinho — não escolhe cidade de
 * fallback, não mostra mensagem: só traduz a API de callback do navegador num estado que
 * `LocationPicker` (T9) consome e interpreta.
 *
 * `unsupported` cobre dois casos com o mesmo tratamento para quem usa o hook: navegador sem
 * `navigator.geolocation` (ex.: contexto inseguro fora de `localhost`, spec "Riscos" R7) e
 * ambiente de teste (jsdom) sem a API implementada.
 */
export type UseGeolocationState =
  | { readonly status: 'idle' }
  | { readonly status: 'requesting' }
  | { readonly status: 'granted'; readonly latitude: number; readonly longitude: number }
  | { readonly status: 'denied' }
  | { readonly status: 'unsupported' }

export interface UseGeolocationResult {
  readonly state: UseGeolocationState
  readonly request: () => void
}

export function useGeolocation(): UseGeolocationResult {
  const [state, setState] = useState<UseGeolocationState>({ status: 'idle' })

  const request = useCallback(() => {
    if (typeof navigator === 'undefined' || !navigator.geolocation) {
      setState({ status: 'unsupported' })
      return
    }

    setState({ status: 'requesting' })

    navigator.geolocation.getCurrentPosition(
      (position) => {
        setState({
          status: 'granted',
          latitude: position.coords.latitude,
          longitude: position.coords.longitude,
        })
      },
      () => {
        // O navegador não distingue "permissão negada" de "posição indisponível"/"timeout" de
        // forma que valha a pena expor na tela (spec J2: mensagem única, sem detalhe técnico).
        setState({ status: 'denied' })
      },
    )
  }, [])

  return { state, request }
}
