import { fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { CityOption } from '../api/search'
import { LocationPicker, type LocationSelection } from './LocationPicker'

const CITIES: readonly CityOption[] = [
  { name: 'Belo Horizonte', state: 'MG', latitude: -19.9245, longitude: -43.9352 },
  { name: 'São Paulo', state: 'SP', latitude: -23.5505, longitude: -46.6333 },
]

type SuccessCallback = (position: GeolocationPosition) => void
type ErrorCallback = (error: GeolocationPositionError) => void

function stubGeolocation(onRequest: (success: SuccessCallback, error: ErrorCallback) => void): void {
  Object.defineProperty(navigator, 'geolocation', {
    value: {
      getCurrentPosition: (success: SuccessCallback, error?: ErrorCallback) => {
        onRequest(success, error ?? (() => {}))
      },
    },
    configurable: true,
  })
}

function fakePosition(latitude: number, longitude: number): GeolocationPosition {
  return { coords: { latitude, longitude } } as unknown as GeolocationPosition
}

/** Harness: o mundo real controla `selection` de fora (App/SearchForm) — reproduzido aqui com useState. */
function Harness() {
  const [selection, setSelection] = useState<LocationSelection>({ kind: 'none' })
  return <LocationPicker cities={CITIES} selection={selection} onSelectionChange={setSelection} />
}

describe('LocationPicker', () => {
  afterEach(() => {
    Reflect.deleteProperty(navigator, 'geolocation')
  })

  it('começa sem localização: aviso de "nenhuma localização" e cidade vazia', () => {
    render(<Harness />)

    expect(screen.getByText(/Nenhuma localização informada/)).toBeDefined()
    expect(screen.getByLabelText<HTMLSelectElement>('Cidade').value).toBe('')
    expect(screen.queryByRole('button', { name: 'Buscar sem localização' })).toBeNull()
  })

  it('mostra "Obtendo localização…" (botão desabilitado) enquanto aguarda o navegador responder', () => {
    stubGeolocation(() => {
      // nunca chama success/error — simula o navegador ainda decidindo (spec J1: "sem travar").
    })
    render(<Harness />)

    fireEvent.click(screen.getByRole('button', { name: 'Usar minha localização' }))

    const button = screen.getByRole<HTMLButtonElement>('button', { name: 'Obtendo localização…' })
    expect(button.disabled).toBe(true)
  })

  it('permissão concedida: repassa coordenadas arredondadas a 3 casas e mostra "sua posição atual"', () => {
    stubGeolocation((success) => {
      success(fakePosition(-19.924567, -43.935187))
    })
    const onSelectionChange = vi.fn()
    function CapturingHarness() {
      const [selection, setSelection] = useState<LocationSelection>({ kind: 'none' })
      return (
        <LocationPicker
          cities={CITIES}
          selection={selection}
          onSelectionChange={(next) => {
            onSelectionChange(next)
            setSelection(next)
          }}
        />
      )
    }
    render(<CapturingHarness />)

    fireEvent.click(screen.getByRole('button', { name: 'Usar minha localização' }))

    expect(screen.getByText('Localização em uso: sua posição atual.')).toBeDefined()
    expect(onSelectionChange).toHaveBeenCalledWith({ kind: 'browser', latitude: -19.925, longitude: -43.935 })
  })

  it('permissão negada: alerta claro (sem detalhe técnico) e foco no seletor de cidade', () => {
    stubGeolocation((_success, error) => {
      error({ code: 1, message: 'User denied Geolocation' } as unknown as GeolocationPositionError)
    })
    render(<Harness />)

    fireEvent.click(screen.getByRole('button', { name: 'Usar minha localização' }))

    const alert = screen.getByRole('alert')
    expect(alert.textContent).toBe('Não foi possível obter sua localização — escolha uma cidade na lista abaixo.')
    expect(document.activeElement).toBe(screen.getByLabelText('Cidade'))
  })

  it('sem suporte a geolocalização no navegador: alerta claro e foco no seletor de cidade, sem quebrar', () => {
    render(<Harness />) // sem stub: navigator.geolocation não existe em jsdom por padrão

    fireEvent.click(screen.getByRole('button', { name: 'Usar minha localização' }))

    expect(screen.getByRole('alert').textContent).toContain('não oferece localização automática')
    expect(document.activeElement).toBe(screen.getByLabelText('Cidade'))
  })

  it('escolher uma cidade atualiza a seleção e mostra a cidade em uso', () => {
    render(<Harness />)

    fireEvent.change(screen.getByLabelText('Cidade'), { target: { value: 'Belo Horizonte|MG' } })

    expect(screen.getByText('Localização em uso: Belo Horizonte, MG.')).toBeDefined()
  })

  it('"Buscar sem localização" volta ao estado "sem localização" depois de uma cidade escolhida', () => {
    render(<Harness />)

    fireEvent.change(screen.getByLabelText('Cidade'), { target: { value: 'Belo Horizonte|MG' } })
    fireEvent.click(screen.getByRole('button', { name: 'Buscar sem localização' }))

    expect(screen.getByText(/Nenhuma localização informada/)).toBeDefined()
    expect(screen.getByLabelText<HTMLSelectElement>('Cidade').value).toBe('')
  })
})
