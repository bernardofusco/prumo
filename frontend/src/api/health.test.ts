import { afterEach, describe, expect, it, vi } from 'vitest'

import { fetchHealth } from './health'

describe('fetchHealth', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('retorna "online" quando a API responde com o contrato de sucesso', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ status: 'ok', service: 'prumo-api' }), { status: 200 }),
      ),
    )

    await expect(fetchHealth()).resolves.toBe('online')
  })

  it('retorna "offline" quando a resposta HTTP não é ok (ex.: 500)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 500 })))

    await expect(fetchHealth()).resolves.toBe('offline')
  })

  it('retorna "offline" sem propagar exceção quando o fetch falha (erro de rede)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(fetchHealth()).resolves.toBe('offline')
  })

  it('retorna "offline" quando o corpo da resposta não tem status "ok"', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ status: 'degraded', database: 'unreachable' }), {
          status: 200,
        }),
      ),
    )

    await expect(fetchHealth()).resolves.toBe('offline')
  })
})
