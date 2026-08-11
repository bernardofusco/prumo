import { useEffect, useRef, type FormEvent } from 'react'

import type { CityOption } from '../api/search'
import { ExampleQueries } from './ExampleQueries'
import { LocationPicker, type LocationSelection } from './LocationPicker'

export interface SearchFormProps {
  readonly query: string
  readonly onQueryChange: (value: string) => void
  readonly fieldError: string | null
  readonly location: LocationSelection
  readonly onLocationChange: (selection: LocationSelection) => void
  readonly cities: readonly CityOption[]
  readonly exampleQueries: readonly string[]
  readonly onExampleQueryClick: (query: string) => void
  readonly onSubmit: () => void
  readonly isSubmitting: boolean
}

const QUERY_ERROR_ID = 'search-query-error'

/**
 * O formulário de busca inteiro (design.md §7; BSC-10/BSC-12): campo rotulado, os três estados de
 * localização (`LocationPicker`) e as consultas de demonstração (`ExampleQueries`) — tudo dentro
 * de um único `<form role="search">`, submetível por Enter (a partir do campo) ou pelo botão.
 *
 * **Busca só no submit** (spec.md "Concorrência"): nenhum handler de `onChange` do campo dispara
 * `onSubmit`; só `handleSubmit` (form) chama.
 */
export function SearchForm({
  query,
  onQueryChange,
  fieldError,
  location,
  onLocationChange,
  cities,
  exampleQueries,
  onExampleQueryClick,
  onSubmit,
  isSubmitting,
}: SearchFormProps) {
  const queryInputRef = useRef<HTMLInputElement>(null)

  // Erro do campo (validação local ou 400 da API): foco vai para o campo, guideline "Errors
  // inline next to fields; focus first error on submit". Roda só quando a MENSAGEM muda — não a
  // cada render — para não roubar foco de quem já está digitando de novo.
  useEffect(() => {
    if (fieldError) {
      queryInputRef.current?.focus()
    }
  }, [fieldError])

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    onSubmit()
  }

  return (
    <form role="search" onSubmit={handleSubmit} className="search-form">
      <div className="search-form__field">
        <label htmlFor="search-query">O que você precisa?</label>
        <input
          id="search-query"
          name="q"
          type="text"
          autoComplete="off"
          ref={queryInputRef}
          value={query}
          onChange={(event) => {
            onQueryChange(event.target.value)
          }}
          aria-describedby={fieldError ? QUERY_ERROR_ID : undefined}
          aria-invalid={fieldError ? true : undefined}
          placeholder="Ex.: vazamento no banheiro…"
        />
        {fieldError && (
          <p id={QUERY_ERROR_ID} role="alert" className="search-form__error">
            {fieldError}
          </p>
        )}
      </div>

      <LocationPicker cities={cities} selection={location} onSelectionChange={onLocationChange} />

      <ExampleQueries queries={exampleQueries} onSelect={onExampleQueryClick} />

      <button type="submit" disabled={isSubmitting}>
        {isSubmitting ? 'Buscando…' : 'Buscar'}
      </button>
    </form>
  )
}
