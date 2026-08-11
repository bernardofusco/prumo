import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Notice } from './Notice'

describe('Notice', () => {
  it('renderiza os filhos dentro de role="status" para tone info', () => {
    render(<Notice tone="info">Sem localização: ordenado só por relevância.</Notice>)

    const notice = screen.getByRole('status')
    expect(notice.textContent).toBe('Sem localização: ordenado só por relevância.')
  })

  it('renderiza role="status" para tone warning (modo degradado)', () => {
    render(<Notice tone="warning">Modo degradado ativo.</Notice>)

    expect(screen.getByRole('status').textContent).toBe('Modo degradado ativo.')
  })

  it('renderiza role="alert" para tone error', () => {
    render(<Notice tone="error">Falha ao buscar profissionais.</Notice>)

    const notice = screen.getByRole('alert')
    expect(notice.textContent).toBe('Falha ao buscar profissionais.')
  })

  it('announce={false}: nenhum role — evita anúncio duplicado dentro de outra região aria-live', () => {
    render(
      <Notice tone="warning" announce={false}>
        Modo degradado ativo.
      </Notice>,
    )

    expect(screen.queryByRole('status')).toBeNull()
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.getByText('Modo degradado ativo.')).toBeDefined()
  })
})
