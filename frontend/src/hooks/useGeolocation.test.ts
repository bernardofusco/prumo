import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { GEOLOCATION_TIMEOUT_MS, useGeolocation } from './useGeolocation'

type SuccessCallback = (position: GeolocationPosition) => void
type ErrorCallback = (error: GeolocationPositionError) => void

function stubGeolocation(
  onRequest: (success: SuccessCallback, error: ErrorCallback) => void,
): void {
  Object.defineProperty(navigator, 'geolocation', {
    value: {
      getCurrentPosition: (success: SuccessCallback, error?: ErrorCallback) => {
        onRequest(success, error ?? (() => {}))
      },
    },
    configurable: true,
  })
}

function fakePosition(latitude: number, longitude: number): GeolocationPosition {
  return { coords: { latitude, longitude } } as unknown as GeolocationPosition
}

function fakeError(): GeolocationPositionError {
  return { code: 1, message: 'User denied Geolocation' } as unknown as GeolocationPositionError
}

describe('useGeolocation', () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, 'geolocation')
  })

  it('começa em "idle"', () => {
    const { result } = renderHook(() => useGeolocation())

    expect(result.current.state.status).toBe('idle')
  })

  it('vai para "unsupported" quando o navegador não expõe geolocation (ex.: contexto inseguro)', () => {
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(result.current.state.status).toBe('unsupported')
  })

  it('vai para "requesting" enquanto aguarda o navegador responder', () => {
    stubGeolocation(() => {
      // Nunca chama success/error — simula o navegador ainda decidindo (spec J1: "sem travar").
    })
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(result.current.state.status).toBe('requesting')
  })

  it('vai para "granted" com latitude/longitude quando o usuário permite', () => {
    stubGeolocation((success) => {
      success(fakePosition(-19.9245, -43.9352))
    })
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(result.current.state).toEqual({
      status: 'granted',
      latitude: -19.9245,
      longitude: -43.9352,
    })
  })

  it('vai para "denied" quando o usuário nega a permissão', () => {
    stubGeolocation((_success, error) => {
      error(fakeError())
    })
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(result.current.state.status).toBe('denied')
  })

  it('passa PositionOptions.timeout explícito para getCurrentPosition', () => {
    let receivedOptions: PositionOptions | undefined
    Object.defineProperty(navigator, 'geolocation', {
      value: {
        getCurrentPosition: (
          _success: SuccessCallback,
          _error?: ErrorCallback,
          options?: PositionOptions,
        ) => {
          receivedOptions = options
        },
      },
      configurable: true,
    })
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(receivedOptions?.timeout).toBe(GEOLOCATION_TIMEOUT_MS)
  })

  it('MET-515: vai para "timeout" (nunca fica preso em "requesting") quando a promessa do navegador nunca resolve nem rejeita', () => {
    vi.useFakeTimers()
    try {
      stubGeolocation(() => {
        // Nunca chama success/error — reproduz o defeito real: perfil gerenciado que nega em
        // silêncio, máquina sem GPS, política do navegador. A UI precisa sair sozinha desse estado.
      })
      const { result } = renderHook(() => useGeolocation())

      act(() => {
        result.current.request()
      })
      expect(result.current.state.status).toBe('requesting')

      act(() => {
        vi.advanceTimersByTime(GEOLOCATION_TIMEOUT_MS)
      })

      expect(result.current.state.status).toBe('timeout')
    } finally {
      vi.useRealTimers()
    }
  })

  it('vai para "timeout" quando o navegador chama error com o código TIMEOUT (respeitou a option)', () => {
    stubGeolocation((_success, error) => {
      error({ code: 3, message: 'Timeout expired' } as unknown as GeolocationPositionError)
    })
    const { result } = renderHook(() => useGeolocation())

    act(() => {
      result.current.request()
    })

    expect(result.current.state.status).toBe('timeout')
  })

  it('MET-515 (revisão): descarta um "success" tardio do navegador que chega depois de o timeout já ter abandonado a tentativa', () => {
    vi.useFakeTimers()
    try {
      let lateSuccess: SuccessCallback | undefined
      stubGeolocation((success) => {
        // Guardado, não chamado "na hora" — o navegador que segura o callback e responde tarde é
        // exatamente a premissa do defeito original (MET-515), não um caso hipotético à parte.
        lateSuccess = success
      })
      const { result } = renderHook(() => useGeolocation())

      act(() => {
        result.current.request()
      })
      act(() => {
        vi.advanceTimersByTime(GEOLOCATION_TIMEOUT_MS)
      })
      expect(result.current.state.status).toBe('timeout')

      act(() => {
        lateSuccess?.(fakePosition(-19.9245, -43.9352))
      })

      // Sem a invalidação do requestId no watchdog, este "success" tardio sobrescreveria o estado
      // para "granted" por cima de uma decisão que o usuário já pode ter tomado (ex.: escolher uma
      // cidade) obedecendo ao alerta de timeout.
      expect(result.current.state.status).toBe('timeout')
    } finally {
      vi.useRealTimers()
    }
  })

  it('MET-515 (revisão): descarta um "error" tardio do navegador que chega depois de o timeout já ter abandonado a tentativa', () => {
    vi.useFakeTimers()
    try {
      let lateError: ErrorCallback | undefined
      stubGeolocation((_success, error) => {
        lateError = error
      })
      const { result } = renderHook(() => useGeolocation())

      act(() => {
        result.current.request()
      })
      act(() => {
        vi.advanceTimersByTime(GEOLOCATION_TIMEOUT_MS)
      })
      expect(result.current.state.status).toBe('timeout')

      act(() => {
        lateError?.(fakeError())
      })

      expect(result.current.state.status).toBe('timeout')
    } finally {
      vi.useRealTimers()
    }
  })

  it('MET-515 (revisão): duas chamadas a request() sem a primeira resolver mantêm só um relógio vivo (o da tentativa anterior não vaza)', () => {
    vi.useFakeTimers()
    try {
      stubGeolocation(() => {
        // Nunca chama success/error em nenhuma das duas tentativas — o hook é API pública,
        // chamável de novo mesmo em sequência que a UI atual não permite (botão fica `disabled`
        // durante "requesting"), mas nada no hook impede outro consumidor de fazer isso.
      })
      const { result, unmount } = renderHook(() => useGeolocation())

      act(() => {
        result.current.request()
      })
      expect(vi.getTimerCount()).toBe(1)

      act(() => {
        result.current.request()
      })
      expect(vi.getTimerCount()).toBe(1) // não 2 — o relógio da primeira tentativa foi cancelado

      unmount()
      expect(vi.getTimerCount()).toBe(0) // nada sobra vivo, nem o relógio mais antigo
    } finally {
      vi.useRealTimers()
    }
  })

  it('MET-515 (revisão): limpa o relógio pendente quando o hook desmonta no meio de uma tentativa "requesting"', () => {
    vi.useFakeTimers()
    try {
      stubGeolocation(() => {
        // Nunca chama success/error — desmontar no meio de "requesting" é um caminho real (ex.:
        // usuário navega para outra tela enquanto o navegador ainda decide).
      })
      const { result, unmount } = renderHook(() => useGeolocation())

      act(() => {
        result.current.request()
      })
      expect(vi.getTimerCount()).toBe(1)

      unmount()

      expect(vi.getTimerCount()).toBe(0)
    } finally {
      vi.useRealTimers()
    }
  })
})
