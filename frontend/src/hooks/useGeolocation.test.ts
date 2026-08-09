import { act, renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { useGeolocation } from './useGeolocation'

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
})
