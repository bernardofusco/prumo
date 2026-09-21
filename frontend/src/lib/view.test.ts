import { describe, expect, it } from 'vitest'

import { parseView, toPath, type View } from './view'

describe('parseView', () => {
  it('reconhece a raiz "/" como a view de busca', () => {
    expect(parseView('/')).toEqual({ kind: 'search' })
  })

  it('reconhece a string vazia também como a view de busca (defensivo)', () => {
    expect(parseView('')).toEqual({ kind: 'search' })
  })

  it('reconhece "/profissional/:slug"', () => {
    expect(parseView('/profissional/ana-ribeiro-bh-01')).toEqual({
      kind: 'professional',
      slug: 'ana-ribeiro-bh-01',
    })
  })

  it('reconhece "/profissional/:slug/agenda"', () => {
    expect(parseView('/profissional/ana-ribeiro-bh-01/agenda')).toEqual({
      kind: 'professionalAgenda',
      slug: 'ana-ribeiro-bh-01',
    })
  })

  it('ignora a barra final — "/profissional/:slug/" é a MESMA view de "/profissional/:slug"', () => {
    expect(parseView('/profissional/ana-ribeiro-bh-01/')).toEqual({
      kind: 'professional',
      slug: 'ana-ribeiro-bh-01',
    })
  })

  it('ignora query string e fragmento', () => {
    expect(parseView('/profissional/ana-ribeiro-bh-01?from=busca#topo')).toEqual({
      kind: 'professional',
      slug: 'ana-ribeiro-bh-01',
    })
  })

  it('decodifica o slug (percent-encoding)', () => {
    expect(parseView('/profissional/jo%C3%A3o-silva')).toEqual({
      kind: 'professional',
      slug: 'joão-silva',
    })
  })

  it('cai para "search" em caminho desconhecido — sem tela de 404 nesta feature', () => {
    expect(parseView('/nao-existe')).toEqual({ kind: 'search' })
  })

  it('cai para "search" quando falta o slug de "/profissional/"', () => {
    expect(parseView('/profissional/')).toEqual({ kind: 'search' })
  })

  it('cai para "search" quando falta o slug de "/profissional//agenda"', () => {
    expect(parseView('/profissional//agenda')).toEqual({ kind: 'search' })
  })

  it('cai para "search" quando há um segmento extra depois de "/agenda"', () => {
    expect(parseView('/profissional/ana-ribeiro-bh-01/agenda/extra')).toEqual({ kind: 'search' })
  })
})

describe('toPath', () => {
  it('a view de busca vira "/"', () => {
    expect(toPath({ kind: 'search' })).toBe('/')
  })

  it('a view de profissional vira "/profissional/:slug"', () => {
    expect(toPath({ kind: 'professional', slug: 'ana-ribeiro-bh-01' })).toBe('/profissional/ana-ribeiro-bh-01')
  })

  it('a view de agenda vira "/profissional/:slug/agenda"', () => {
    expect(toPath({ kind: 'professionalAgenda', slug: 'ana-ribeiro-bh-01' })).toBe(
      '/profissional/ana-ribeiro-bh-01/agenda',
    )
  })

  it('codifica o slug (percent-encoding) — o inverso exato de parseView', () => {
    expect(toPath({ kind: 'professional', slug: 'joão-silva' })).toBe('/profissional/jo%C3%A3o-silva')
  })
})

describe('parseView(toPath(view)) é a identidade', () => {
  const views: readonly View[] = [
    { kind: 'search' },
    { kind: 'professional', slug: 'ana-ribeiro-bh-01' },
    { kind: 'professionalAgenda', slug: 'ana-ribeiro-bh-01' },
    { kind: 'professional', slug: 'joão-silva' },
  ]

  it.each(views)('round-trip de %o', (view) => {
    expect(parseView(toPath(view))).toEqual(view)
  })
})
