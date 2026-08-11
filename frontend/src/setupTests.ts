import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// `globals` do Vitest fica desligado (imports explícitos de describe/it/expect nos
// testes), então o auto-cleanup do Testing Library não se registra sozinho — fazemos
// isso aqui para não vazar DOM de um teste para o outro.
afterEach(() => {
  cleanup()
})
