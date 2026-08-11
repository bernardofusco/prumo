import { describe, expect, it } from 'vitest'

import { formatDistanceKm, formatScore } from './format'

describe('formatDistanceKm', () => {
  it('formata com 1 casa decimal e vírgula (pt-BR)', () => {
    expect(formatDistanceKm(3.42)).toBe('3,4')
  })

  it('arredonda em vez de truncar', () => {
    expect(formatDistanceKm(3.45)).toBe('3,5')
  })

  it('mantém a casa decimal mesmo em número inteiro', () => {
    expect(formatDistanceKm(10)).toBe('10,0')
  })
})

describe('formatScore', () => {
  it('formata com 2 casas decimais e vírgula (pt-BR)', () => {
    expect(formatScore(0.8231)).toBe('0,82')
  })

  it('arredonda em vez de truncar', () => {
    expect(formatScore(0.7119)).toBe('0,71')
  })

  it('mantém as duas casas decimais mesmo em número inteiro', () => {
    expect(formatScore(1)).toBe('1,00')
  })
})
