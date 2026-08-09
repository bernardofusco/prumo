import { useEffect, useRef, type ChangeEvent } from 'react'

import type { CityOption } from '../api/search'
import { roundCoordinate } from '../lib/geo'
import { useGeolocation } from '../hooks/useGeolocation'

/**
 * Os três estados de localização da busca (spec.md D4/F1/F2, BSC-11). `'none'` é o estado padrão
 * e também o estado explícito de "sem localização" — não há uma quarta variante escondida.
 */
export type LocationSelection =
  | { readonly kind: 'none' }
  | { readonly kind: 'browser'; readonly latitude: number; readonly longitude: number }
  | { readonly kind: 'city'; readonly city: CityOption }

export interface LocationPickerProps {
  readonly cities: readonly CityOption[]
  readonly selection: LocationSelection
  readonly onSelectionChange: (selection: LocationSelection) => void
}

function cityKey(city: CityOption): string {
  return `${city.name}|${city.state}`
}

export function LocationPicker({ cities, selection, onSelectionChange }: LocationPickerProps) {
  const geolocation = useGeolocation()
  const citySelectRef = useRef<HTMLSelectElement>(null)

  // `onSelectionChange` NÃO entra nas deps do efeito abaixo — só a "versão mais recente" via ref.
  // Se entrasse, um chamador que passa uma função inline (nova referência a cada render — ex.:
  // `onSelectionChange={(v) => { ...; setState(v) }}`) recriaria a função, o efeito rodaria de
  // novo porque a dependência mudou, chamaria `onSelectionChange` de novo, o pai re-renderizaria
  // com OUTRA função nova — loop infinito enquanto `geolocation.state.status === 'granted'`
  // (que nunca deixa de ser verdade sozinho). `setLocation` do `useState` é estável e não cairia
  // nisso, mas nada nesta função deveria depender de o chamador lembrar disso.
  const onSelectionChangeRef = useRef(onSelectionChange)
  useEffect(() => {
    onSelectionChangeRef.current = onSelectionChange
  })

  // Permissão concedida: o navegador respondeu DEPOIS deste componente já ter montado (é async
  // por natureza) — repassa a coordenada para quem é dono da seleção (SearchForm/App) assim que
  // ela chega. `roundCoordinate` já entra aqui (não só em api/search.ts): a coordenada bruta do
  // navegador nunca precisa passar por mais nenhum estado do app sem estar arredondada.
  useEffect(() => {
    if (geolocation.state.status === 'granted') {
      onSelectionChangeRef.current({
        kind: 'browser',
        latitude: roundCoordinate(geolocation.state.latitude),
        longitude: roundCoordinate(geolocation.state.longitude),
      })
    }
  }, [geolocation.state])

  // Permissão negada (ou geolocalização indisponível/sem suporte): spec.md J2 — "mensagem clara,
  // sem erro técnico na tela, sem travamento, foco levado ao seletor de cidade".
  useEffect(() => {
    if (geolocation.state.status === 'denied' || geolocation.state.status === 'unsupported') {
      citySelectRef.current?.focus()
    }
  }, [geolocation.state.status])

  function handleUseMyLocationClick() {
    geolocation.request()
  }

  function handleCityChange(event: ChangeEvent<HTMLSelectElement>) {
    const value = event.target.value

    if (value === '') {
      onSelectionChange({ kind: 'none' })
      return
    }

    const city = cities.find((candidate) => cityKey(candidate) === value)
    if (city) {
      onSelectionChange({ kind: 'city', city })
    }
  }

  function handleClearClick() {
    onSelectionChange({ kind: 'none' })
  }

  const isRequesting = geolocation.state.status === 'requesting'
  const citySelectValue = selection.kind === 'city' ? cityKey(selection.city) : ''

  return (
    <div className="location-picker">
      <span id="location-picker-label" className="location-picker__label">
        Localização
      </span>

      <div role="group" aria-labelledby="location-picker-label" className="location-picker__controls">
        <button type="button" onClick={handleUseMyLocationClick} disabled={isRequesting}>
          {isRequesting ? 'Obtendo localização…' : 'Usar minha localização'}
        </button>

        <label htmlFor="location-city">Cidade</label>
        <select id="location-city" ref={citySelectRef} value={citySelectValue} onChange={handleCityChange}>
          <option value="">Selecione uma cidade…</option>
          {cities.map((city) => (
            <option key={cityKey(city)} value={cityKey(city)}>
              {city.name} — {city.state}
            </option>
          ))}
        </select>

        {selection.kind !== 'none' && (
          <button type="button" onClick={handleClearClick}>
            Buscar sem localização
          </button>
        )}
      </div>

      <p className="location-picker__status" role="status">
        {selection.kind === 'none' &&
          'Nenhuma localização informada — os resultados serão ordenados só por relevância.'}
        {selection.kind === 'browser' && 'Localização em uso: sua posição atual.'}
        {selection.kind === 'city' && `Localização em uso: ${selection.city.name}, ${selection.city.state}.`}
      </p>

      {geolocation.state.status === 'denied' && selection.kind !== 'city' && (
        <p role="alert">Não foi possível obter sua localização — escolha uma cidade na lista abaixo.</p>
      )}

      {geolocation.state.status === 'unsupported' && selection.kind !== 'city' && (
        <p role="alert">Seu navegador não oferece localização automática aqui — escolha uma cidade na lista abaixo.</p>
      )}
    </div>
  )
}
