import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { ExampleQueries } from './ExampleQueries'

describe('ExampleQueries', () => {
  it('não renderiza nada (nem rótulo, nem lista) quando não há consultas', () => {
    const { container } = render(<ExampleQueries queries={[]} onSelect={vi.fn()} />)

    expect(container.innerHTML).toBe('')
  })

  it('renderiza um botão clicável por consulta', () => {
    render(
      <ExampleQueries
        queries={['vazamento no banheiro', 'meu chuveiro não esquenta']}
        onSelect={vi.fn()}
      />,
    )

    expect(screen.getByRole('button', { name: 'vazamento no banheiro' })).toBeDefined()
    expect(screen.getByRole('button', { name: 'meu chuveiro não esquenta' })).toBeDefined()
  })

  it('chama onSelect com o texto da consulta clicada', () => {
    const onSelect = vi.fn()
    render(<ExampleQueries queries={['vazamento no banheiro']} onSelect={onSelect} />)

    fireEvent.click(screen.getByRole('button', { name: 'vazamento no banheiro' }))

    expect(onSelect).toHaveBeenCalledTimes(1)
    expect(onSelect).toHaveBeenCalledWith('vazamento no banheiro')
  })
})
