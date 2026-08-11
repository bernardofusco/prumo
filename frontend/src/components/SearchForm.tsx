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
  /**
   * `Search:MaxQueryLength` do servidor, ecoado por `GET /api/search/options` (MET-516) —
   * `null` enquanto as opções ainda não chegaram (ou falharam): sem o número real, o campo não
   * aplica limite nenhum nem mostra o contador — nunca um valor "chutado" localmente, que poderia
   * divergir do que o servidor de fato aceita.
   */
  readonly maxQueryLength: number | null
}

const QUERY_ERROR_ID = 'search-query-error'
const LENGTH_HINT_ID = 'search-query-length-hint'

/**
 * A partir de quantos caracteres RESTANTES o contador aparece (MET-516: "sinalize a proximidade do
 * limite de forma discreta") — abaixo disso o campo fica silencioso, exatamente como antes. 20 é um
 * valor fixo, não uma fração de `maxQueryLength`: um aviso a "10% do limite" seria imperceptível para
 * um `maxQueryLength` pequeno e apareceria cedo demais para um limite grande; a distância absoluta é
 * o que importa para quem está prestes a estourar o campo, não a proporção.
 */
const LENGTH_HINT_THRESHOLD = 20

/** Texto do contador discreto (MET-516) — singular/plural, e uma frase própria para o limite exato. */
function formatRemainingCharactersHint(remainingCharacters: number): string {
  if (remainingCharacters <= 0) {
    return 'Você atingiu o limite de caracteres da busca.'
  }

  return remainingCharacters === 1
    ? 'Falta 1 caractere para o limite da busca.'
    : `Faltam ${remainingCharacters} caracteres para o limite da busca.`
}

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
  maxQueryLength,
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

  // Contador discreto (MET-516): só existe quando o servidor já disse o limite real
  // (GET /api/search/options) — nunca um número "chutado" localmente — e só aparece perto do
  // limite (ver LENGTH_HINT_THRESHOLD), silencioso no resto do tempo.
  const remainingCharacters = maxQueryLength === null ? null : maxQueryLength - query.length
  const showLengthHint = remainingCharacters !== null && remainingCharacters <= LENGTH_HINT_THRESHOLD

  const describedByIds = [fieldError ? QUERY_ERROR_ID : null, showLengthHint ? LENGTH_HINT_ID : null]
    .filter((id): id is string => id !== null)
    .join(' ')

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
          maxLength={maxQueryLength ?? undefined}
          aria-describedby={describedByIds || undefined}
          aria-invalid={fieldError ? true : undefined}
          placeholder="Ex.: vazamento no banheiro…"
        />
        {fieldError && (
          <p id={QUERY_ERROR_ID} role="alert" className="search-form__error">
            {fieldError}
          </p>
        )}
        {showLengthHint && (
          <p id={LENGTH_HINT_ID} className="search-form__length-hint">
            {formatRemainingCharactersHint(remainingCharacters)}
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
