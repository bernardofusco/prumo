import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { vi } from 'vitest'

import { getClientKey } from './clientKey'

const STORAGE_KEY = 'prumo.clientKey'
const UUID_V4_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

/**
 * `localStorage` FAKE (instrução explícita da task, não o `localStorage` de verdade do jsdom): um
 * `Map` em memória atrás da interface `Storage` — suficiente para `getClientKey`, isolado entre
 * testes (cada `beforeEach` troca por uma instância nova, o que também simula "outro browser").
 */
function createFakeLocalStorage(): Storage {
  const store = new Map<string, string>()

  return {
    getItem: (key: string) => (store.has(key) ? (store.get(key) ?? null) : null),
    setItem: (key: string, value: string) => {
      store.set(key, value)
    },
    removeItem: (key: string) => {
      store.delete(key)
    },
    clear: () => {
      store.clear()
    },
    key: (index: number) => Array.from(store.keys())[index] ?? null,
    get length() {
      return store.size
    },
  }
}

describe('getClientKey', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', createFakeLocalStorage())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('cria um UUID v4 na primeira chamada e grava em localStorage sob "prumo.clientKey"', () => {
    const key = getClientKey()

    expect(key).toMatch(UUID_V4_PATTERN)
    expect(localStorage.getItem(STORAGE_KEY)).toBe(key)
  })

  it('relê o MESMO valor em chamadas seguintes — não cria uma segunda chave', () => {
    const first = getClientKey()
    const second = getClientKey()
    const third = getClientKey()

    expect(second).toBe(first)
    expect(third).toBe(first)
  })

  it('relê o valor já existente em localStorage em vez de gerar um novo', () => {
    const preExisting = '11111111-1111-4111-8111-111111111111'
    localStorage.setItem(STORAGE_KEY, preExisting)

    expect(getClientKey()).toBe(preExisting)
  })

  it('gera e persiste uma nova chave quando o valor salvo não é um UUID v4 válido (dado corrompido)', () => {
    localStorage.setItem(STORAGE_KEY, 'nao-e-um-uuid')

    const key = getClientKey()

    expect(key).not.toBe('nao-e-um-uuid')
    expect(key).toMatch(UUID_V4_PATTERN)
    expect(localStorage.getItem(STORAGE_KEY)).toBe(key)
  })

  it('duas "instâncias" de browser (localStorage vazio cada uma) produzem clientKeys diferentes', () => {
    const first = getClientKey()

    vi.stubGlobal('localStorage', createFakeLocalStorage())
    const second = getClientKey()

    expect(second).not.toBe(first)
  })
})
