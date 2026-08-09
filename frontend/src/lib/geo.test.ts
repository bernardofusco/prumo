import { describe, expect, it } from 'vitest'

import { roundCoordinate } from './geo'

describe('roundCoordinate', () => {
  it('arredonda para exatamente 3 casas decimais', () => {
    expect(roundCoordinate(-19.924512)).toBe(-19.925)
    expect(roundCoordinate(-43.935187)).toBe(-43.935)
  })

  it('não introduz uma 4ª casa decimal (mutante "mais casas" precisa reprovar)', () => {
    const rounded = roundCoordinate(-19.924567)
    const decimalPlaces = rounded.toString().split('.')[1]?.length ?? 0

    expect(decimalPlaces).toBeLessThanOrEqual(3)
    expect(rounded).toBe(-19.925)
  })

  it('preserva um valor que já tem 3 casas ou menos', () => {
    expect(roundCoordinate(-19.9)).toBe(-19.9)
    expect(roundCoordinate(0)).toBe(0)
  })

  it('arredonda coordenadas positivas', () => {
    expect(roundCoordinate(48.858372)).toBe(48.858)
  })
})
