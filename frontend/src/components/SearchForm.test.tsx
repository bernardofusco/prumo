import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { SearchForm } from './SearchForm'

function renderForm(overrides: Partial<Parameters<typeof SearchForm>[0]> = {}) {
  const props = {
    query: '',
    onQueryChange: vi.fn(),
    fieldError: null,
    location: { kind: 'none' as const },
    onLocationChange: vi.fn(),
    cities: [],
    exampleQueries: [],
    onExampleQueryClick: vi.fn(),
    onSubmit: vi.fn(),
    isSubmitting: false,
    maxQueryLength: null,
    ...overrides,
  }
  render(<SearchForm {...props} />)
  return props
}

describe('SearchForm', () => {
  it('é um <form role="search"> com o campo rotulado de verdade (não por placeholder)', () => {
    renderForm()

    const form = screen.getByRole('search')
    expect(form.tagName).toBe('FORM')

    const input = screen.getByLabelText('O que você precisa?')
    expect(input.tagName).toBe('INPUT')
  })

  it('digitar NÃO dispara busca — só onQueryChange (busca é só no submit)', () => {
    const props = renderForm()

    fireEvent.change(screen.getByLabelText('O que você precisa?'), { target: { value: 'vazamento' } })

    expect(props.onQueryChange).toHaveBeenCalledWith('vazamento')
    expect(props.onSubmit).not.toHaveBeenCalled()
  })

  it('submeter pelo botão chama onSubmit', () => {
    const props = renderForm({ query: 'vazamento no banheiro' })

    fireEvent.click(screen.getByRole('button', { name: 'Buscar' }))

    expect(props.onSubmit).toHaveBeenCalledTimes(1)
  })

  it('submeter com Enter a partir do campo chama onSubmit (submissão nativa do form)', () => {
    const props = renderForm({ query: 'vazamento no banheiro' })

    fireEvent.submit(screen.getByRole('search'))

    expect(props.onSubmit).toHaveBeenCalledTimes(1)
  })

  it('botão de busca mostra "Buscando…" e fica desabilitado durante isSubmitting', () => {
    renderForm({ isSubmitting: true })

    const button = screen.getByRole<HTMLButtonElement>('button', { name: 'Buscando…' })
    expect(button.disabled).toBe(true)
  })

  it('erro do campo: role="alert", ligado por aria-describedby e foco no campo', () => {
    renderForm({ fieldError: 'Digite ao menos 2 caracteres.' })

    const input = screen.getByLabelText('O que você precisa?')
    const error = screen.getByRole('alert')

    expect(error.textContent).toBe('Digite ao menos 2 caracteres.')
    expect(input.getAttribute('aria-describedby')).toBe(error.id)
    expect(document.activeElement).toBe(input)
  })

  it('sem erro: nenhum role="alert" e sem aria-describedby', () => {
    renderForm({ fieldError: null })

    expect(screen.queryByRole('alert')).toBeNull()
    const input = screen.getByLabelText('O que você precisa?')
    expect(input.hasAttribute('aria-describedby')).toBe(false)
  })

  it('clicar numa consulta de demonstração chama onExampleQueryClick com o texto', () => {
    const props = renderForm({ exampleQueries: ['vazamento no banheiro'] })

    fireEvent.click(screen.getByRole('button', { name: 'vazamento no banheiro' }))

    expect(props.onExampleQueryClick).toHaveBeenCalledWith('vazamento no banheiro')
  })

  it('renderiza o LocationPicker (composição, não reimplementação)', () => {
    renderForm()

    expect(screen.getByRole('button', { name: 'Usar minha localização' })).toBeDefined()
    expect(screen.getByLabelText('Cidade')).toBeDefined()
  })

  // ---- MET-516: limite máximo de caracteres, ecoado por GET /api/search/options ------------------

  it('sem maxQueryLength (opções ainda não chegaram): campo sem maxLength e sem contador', () => {
    renderForm({ maxQueryLength: null, query: 'a'.repeat(50) })

    const input = screen.getByLabelText<HTMLInputElement>('O que você precisa?')
    expect(input.hasAttribute('maxlength')).toBe(false)
    expect(screen.queryByText(/caractere/)).toBeNull()
  })

  it('aplica maxLength no campo — o navegador nunca deixa digitar além do limite do servidor', () => {
    renderForm({ maxQueryLength: 200 })

    const input = screen.getByLabelText<HTMLInputElement>('O que você precisa?')
    expect(input.maxLength).toBe(200)
  })

  it('longe do limite: nenhum contador aparece (discreto de verdade)', () => {
    renderForm({ maxQueryLength: 200, query: 'vazamento no banheiro' })

    expect(screen.queryByText(/caractere/)).toBeNull()
  })

  it('perto do limite: contador aparece com a contagem restante', () => {
    renderForm({ maxQueryLength: 200, query: 'a'.repeat(185) })

    expect(screen.getByText('Faltam 15 caracteres para o limite da busca.')).toBeDefined()
  })

  it('a 1 caractere do limite: mensagem no singular', () => {
    renderForm({ maxQueryLength: 200, query: 'a'.repeat(199) })

    expect(screen.getByText('Falta 1 caractere para o limite da busca.')).toBeDefined()
  })

  it('no limite exato: mensagem de limite atingido, não "Faltam 0"', () => {
    renderForm({ maxQueryLength: 200, query: 'a'.repeat(200) })

    expect(screen.getByText('Você atingiu o limite de caracteres da busca.')).toBeDefined()
  })

  it('contador liga aria-describedby do campo ao seu próprio id', () => {
    renderForm({ maxQueryLength: 200, query: 'a'.repeat(195) })

    const input = screen.getByLabelText('O que você precisa?')
    const hint = screen.getByText('Faltam 5 caracteres para o limite da busca.')

    expect(input.getAttribute('aria-describedby')).toBe(hint.id)
  })

  it('erro do campo E contador juntos: aria-describedby lista os dois ids', () => {
    renderForm({ maxQueryLength: 200, query: 'a'.repeat(195), fieldError: 'Erro qualquer.' })

    const input = screen.getByLabelText('O que você precisa?')
    const error = screen.getByRole('alert')
    const hint = screen.getByText('Faltam 5 caracteres para o limite da busca.')

    expect(input.getAttribute('aria-describedby')).toBe(`${error.id} ${hint.id}`)
  })
})
