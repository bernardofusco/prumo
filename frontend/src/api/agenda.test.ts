import { afterEach, describe, expect, it, vi } from 'vitest'

import { CLIENT_KEY_HEADER_NAME, deleteSlot, fetchProfessionalSlots, publishSlot, reserveSlot } from './agenda'

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

const VALID_SLOTS_BODY = {
  professional: { slug: 'ana-ribeiro-bh-01', fullName: 'Ana Ribeiro', specialty: 'Encanador' },
  timezone: 'America/Sao_Paulo',
  slots: [
    { id: 42, start: '2026-10-03T12:00:00Z', end: '2026-10-03T13:00:00Z', status: 'available' },
    { id: 43, start: '2026-10-03T13:00:00Z', end: '2026-10-03T14:00:00Z', status: 'booked' },
  ],
}

const VALID_RESERVE_BODY = {
  reservationId: 7,
  slotId: 42,
  professionalSlug: 'ana-ribeiro-bh-01',
  start: '2026-10-03T12:00:00Z',
  end: '2026-10-03T13:00:00Z',
  replay: false,
}

const VALID_SLOT_ITEM_BODY = { id: 99, start: '2026-10-04T12:00:00Z', end: '2026-10-04T13:00:00Z', status: 'available' }

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('fetchProfessionalSlots', () => {
  it('monta a URL com o slug codificado e sem parâmetros de janela quando não informados', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SLOTS_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchProfessionalSlots('ana-ribeiro-bh-01')

    const [url] = fetchMock.mock.calls[0] as [string]
    const parsed = new URL(url, 'http://localhost')
    expect(parsed.pathname).toBe('/api/professionals/ana-ribeiro-bh-01/slots')
    expect(parsed.search).toBe('')
  })

  it('inclui "from"/"to" quando informados', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SLOTS_BODY))
    vi.stubGlobal('fetch', fetchMock)

    await fetchProfessionalSlots('ana-ribeiro-bh-01', {
      from: '2026-10-01T00:00:00Z',
      to: '2026-10-08T00:00:00Z',
    })

    const [url] = fetchMock.mock.calls[0] as [string]
    const parsed = new URL(url, 'http://localhost')
    expect(parsed.searchParams.get('from')).toBe('2026-10-01T00:00:00Z')
    expect(parsed.searchParams.get('to')).toBe('2026-10-08T00:00:00Z')
  })

  it('repassa o AbortSignal ao fetch', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SLOTS_BODY))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await fetchProfessionalSlots('ana-ribeiro-bh-01', {}, controller.signal)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })

  it('retorna ok:true com os slots (status já vem calculado pela API — não recalculado aqui)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(VALID_SLOTS_BODY)))

    const result = await fetchProfessionalSlots('ana-ribeiro-bh-01')

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.professional.slug).toBe('ana-ribeiro-bh-01')
      expect(result.data.slots).toHaveLength(2)
      expect(result.data.slots[0]?.status).toBe('available')
      expect(result.data.slots[1]?.status).toBe('booked')
    }
  })

  it('retorna code "not_found" tipado para 404', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(404, 'not_found')))

    const result = await fetchProfessionalSlots('slug-desconhecido')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('not_found')
    }
  })

  it('retorna code "invalid_request" tipado para 400', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(400, 'invalid_request')))

    const result = await fetchProfessionalSlots('ana-ribeiro-bh-01', { from: 'nao-e-uma-data' })

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('invalid_request')
    }
  })

  it('retorna "network" quando o corpo 200 vem em formato inesperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ oops: true })))

    const result = await fetchProfessionalSlots('ana-ribeiro-bh-01')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" quando um slot tem "status" fora do vocabulário conhecido', async () => {
    const malformed = { ...VALID_SLOTS_BODY, slots: [{ ...VALID_SLOTS_BODY.slots[0], status: 'sei-la' }] }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(malformed)))

    const result = await fetchProfessionalSlots('ana-ribeiro-bh-01')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando o fetch rejeita (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const result = await fetchProfessionalSlots('ana-ribeiro-bh-01')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })
})

describe('reserveSlot', () => {
  it('envia o cabeçalho X-Prumo-Client-Key e o corpo {"slotId"}', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_RESERVE_BODY, 201))
    vi.stubGlobal('fetch', fetchMock)

    await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/reservations')
    expect(init.method).toBe('POST')
    const headers = new Headers(init.headers)
    expect(headers.get(CLIENT_KEY_HEADER_NAME)).toBe('11111111-1111-4111-8111-111111111111')
    expect(JSON.parse(init.body as string)).toEqual({ slotId: 42 })
  })

  it('repassa o AbortSignal ao fetch', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_RESERVE_BODY, 201))
    vi.stubGlobal('fetch', fetchMock)
    const controller = new AbortController()

    await reserveSlot(42, '11111111-1111-4111-8111-111111111111', controller.signal)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(init.signal).toBe(controller.signal)
  })

  it('201 (criação nova) retorna ok:true com replay:false', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(VALID_RESERVE_BODY, 201)))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.replay).toBe(false)
      expect(result.data.reservationId).toBe(7)
    }
  })

  it('200 (replay do mesmo cliente) retorna ok:true com replay:true — não é tratado como erro', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ ...VALID_RESERVE_BODY, replay: true }, 200)))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.replay).toBe(true)
    }
  })

  it('409 retorna code "slot_conflict"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(409, 'slot_conflict')))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('slot_conflict')
    }
  })

  it('422 retorna code "slot_not_bookable"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(422, 'slot_not_bookable')))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('slot_not_bookable')
    }
  })

  it('404 retorna code "not_found"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(404, 'not_found')))

    const result = await reserveSlot(999, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('not_found')
    }
  })

  it('400 retorna code "invalid_request"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(400, 'invalid_request')))

    const result = await reserveSlot(42, 'chave-invalida')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('invalid_request')
    }
  })

  it('503 (handler genérico, spec.md D5/AGN-12) retorna code "service_unavailable" — não confundido com "slot_conflict"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(503, 'service_unavailable')))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('service_unavailable')
    }
  })

  it('degrada para "network" quando o code de erro não é um dos conhecidos', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(500, 'internal_error')))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" quando o corpo 201 vem em formato inesperado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ oops: true }, 201)))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })

  it('retorna "network" sem lançar exceção quando o fetch rejeita (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const result = await reserveSlot(42, '11111111-1111-4111-8111-111111111111')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })
})

describe('publishSlot', () => {
  it('envia POST com o corpo {"start","end"} para /api/professionals/{slug}/slots', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(VALID_SLOT_ITEM_BODY, 201))
    vi.stubGlobal('fetch', fetchMock)

    await publishSlot('ana-ribeiro-bh-01', '2026-10-04T12:00:00Z', '2026-10-04T13:00:00Z')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/professionals/ana-ribeiro-bh-01/slots')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({
      start: '2026-10-04T12:00:00Z',
      end: '2026-10-04T13:00:00Z',
    })
  })

  it('201 retorna ok:true com o slot criado', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(VALID_SLOT_ITEM_BODY, 201)))

    const result = await publishSlot('ana-ribeiro-bh-01', '2026-10-04T12:00:00Z', '2026-10-04T13:00:00Z')

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.data.id).toBe(99)
      expect(result.data.status).toBe('available')
    }
  })

  it('409 retorna code "slot_overlap"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(409, 'slot_overlap')))

    const result = await publishSlot('ana-ribeiro-bh-01', '2026-10-04T12:00:00Z', '2026-10-04T13:00:00Z')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('slot_overlap')
    }
  })

  it('422 retorna code "slot_not_bookable"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(422, 'slot_not_bookable')))

    const result = await publishSlot('ana-ribeiro-bh-01', '2020-01-01T12:00:00Z', '2020-01-01T13:00:00Z')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('slot_not_bookable')
    }
  })

  it('404 retorna code "not_found"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(404, 'not_found')))

    const result = await publishSlot('slug-desconhecido', '2026-10-04T12:00:00Z', '2026-10-04T13:00:00Z')

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('not_found')
    }
  })
})

describe('deleteSlot', () => {
  it('envia DELETE para /api/professionals/{slug}/slots/{slotId}', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await deleteSlot('ana-ribeiro-bh-01', 42)

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(new URL(url, 'http://localhost').pathname).toBe('/api/professionals/ana-ribeiro-bh-01/slots/42')
    expect(init.method).toBe('DELETE')
  })

  it('204 retorna ok:true sem corpo', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    const result = await deleteSlot('ana-ribeiro-bh-01', 42)

    expect(result).toEqual({ ok: true })
  })

  it('404 retorna code "not_found"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(404, 'not_found')))

    const result = await deleteSlot('ana-ribeiro-bh-01', 999)

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('not_found')
    }
  })

  it('409 retorna code "slot_has_reservation"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(problemResponse(409, 'slot_has_reservation')))

    const result = await deleteSlot('ana-ribeiro-bh-01', 42)

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('slot_has_reservation')
    }
  })

  it('retorna "network" sem lançar exceção quando o fetch rejeita (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const result = await deleteSlot('ana-ribeiro-bh-01', 42)

    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.error.code).toBe('network')
    }
  })
})
