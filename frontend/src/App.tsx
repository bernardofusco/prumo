import { useEffect, useState } from 'react'

import './App.css'
import { fetchHealth } from './api/health'
import { fetchSearchOptions, type SearchOptionsResponse, type SearchParams } from './api/search'
import { ExampleQueries } from './components/ExampleQueries'
import type { LocationSelection } from './components/LocationPicker'
import { Notice } from './components/Notice'
import { ResultList } from './components/ResultList'
import { SearchForm } from './components/SearchForm'
import { StatusIndicator, type StatusIndicatorState } from './components/StatusIndicator'
import { useSearch } from './hooks/useSearch'

type OptionsState =
  | { readonly status: 'loading' }
  | { readonly status: 'success'; readonly data: SearchOptionsResponse }
  | { readonly status: 'error' }

const EMPTY_CITIES: SearchOptionsResponse['cities'] = []
const EMPTY_EXAMPLE_QUERIES: SearchOptionsResponse['exampleQueries'] = []

const QUERY_TOO_SHORT_MESSAGE =
  'Digite ao menos 2 caracteres para buscar — por exemplo, "vazamento no banheiro".'

function buildSearchParams(query: string, location: LocationSelection): SearchParams {
  if (location.kind === 'browser') {
    return { q: query, lat: location.latitude, lng: location.longitude }
  }

  if (location.kind === 'city') {
    return { q: query, lat: location.city.latitude, lng: location.city.longitude }
  }

  return { q: query }
}

function App() {
  const [healthState, setHealthState] = useState<StatusIndicatorState>('checking')
  const [optionsState, setOptionsState] = useState<OptionsState>({ status: 'loading' })
  const [query, setQuery] = useState('')
  const [location, setLocation] = useState<LocationSelection>({ kind: 'none' })
  // Erro do campo por validação LOCAL (ex.: consulta curta demais) — setState de verdade, porque
  // nasce de um evento (submit), não de sincronizar com outro estado.
  const [clientFieldError, setClientFieldError] = useState<string | null>(null)
  const { state: searchState, search } = useSearch()

  useEffect(() => {
    let cancelled = false

    const checkHealth = async () => {
      const result = await fetchHealth()
      if (!cancelled) {
        setHealthState(result)
      }
    }

    void checkHealth()

    return () => {
      cancelled = true
    }
  }, [])

  // GET /api/search/options (design.md §7, spec.md D9): o que a tela precisa para se montar
  // (cidades + consultas de demonstração + modo de embedding), numa chamada só. Falha aqui não
  // impede a busca por texto — só degrada (sem cidades, sem consultas clicáveis).
  useEffect(() => {
    const controller = new AbortController()
    let cancelled = false

    const loadOptions = async () => {
      const result = await fetchSearchOptions(controller.signal)
      if (cancelled) {
        return
      }
      setOptionsState(result.ok ? { status: 'success', data: result.data } : { status: 'error' })
    }

    void loadOptions()

    return () => {
      cancelled = true
      controller.abort()
    }
  }, [])

  // Um 400 da API (`invalid_request`) é, na prática quase sempre, sobre o campo `q` — a única
  // validação que a UI não intercepta antes de sair do browser (BSC-08). Vira erro DO CAMPO, com
  // o texto que a própria API já escreveu em pt-BR — nada de reescrever a mensagem aqui.
  //
  // DERIVADO no render, não sincronizado por `useEffect`: o valor é uma função pura de
  // `searchState` (react-hooks/set-state-in-effect — "You Might Not Need an Effect"). `search-
  // State` já é a fonte da verdade; duplicar em outro `useState` só criaria uma segunda fonte
  // que pode ficar dessincronizada e um render em cascata.
  const serverFieldError =
    searchState.status === 'error' && searchState.error.code === 'invalid_request'
      ? searchState.error.detail
      : null
  const fieldError = clientFieldError ?? serverFieldError

  function handleSubmit() {
    const trimmed = query.trim()

    if (trimmed.length < 2) {
      setClientFieldError(QUERY_TOO_SHORT_MESSAGE)
      return
    }

    setClientFieldError(null)
    search(buildSearchParams(trimmed, location))
  }

  function handleExampleQueryClick(value: string) {
    setQuery(value)
    setClientFieldError(null)
  }

  const cities = optionsState.status === 'success' ? optionsState.data.cities : EMPTY_CITIES
  const exampleQueries =
    optionsState.status === 'success' ? optionsState.data.exampleQueries : EMPTY_EXAMPLE_QUERIES
  // null enquanto as opções não chegaram (ou falharam) — SearchForm não aplica maxLength/contador
  // sem o valor real do servidor (MET-516, XML-doc de SearchFormProps.maxQueryLength).
  const maxQueryLength = optionsState.status === 'success' ? optionsState.data.maxQueryLength : null

  return (
    <main>
      <h1>Prumo</h1>
      <p className="lede">
        Descreva o serviço que você precisa — a busca entende o pedido, não só palavras-chave.
      </p>

      <SearchForm
        query={query}
        onQueryChange={setQuery}
        fieldError={fieldError}
        location={location}
        onLocationChange={setLocation}
        cities={cities}
        exampleQueries={exampleQueries}
        onExampleQueryClick={handleExampleQueryClick}
        onSubmit={handleSubmit}
        isSubmitting={searchState.status === 'loading'}
        maxQueryLength={maxQueryLength}
      />

      {optionsState.status === 'error' && (
        <Notice tone="warning">
          Não foi possível carregar as cidades e as consultas de demonstração agora. A busca por
          texto continua funcionando normalmente.
        </Notice>
      )}

      {/*
        Região viva PERSISTENTE (BSC-12; achado do Reviewer): montada desde o carregamento
        inicial da tela — mesmo vazia, no estado `idle` — para que o leitor de tela já a conheça
        antes de qualquer conteúdo mudar dentro dela. Uma região que só é inserida no DOM já com
        texto dentro ("nasce junto com o conteúdo") costuma não ser anunciada. `:not(:empty)` no
        CSS (App.css) evita que ela ocupe espaço visual enquanto está vazia.
        `announce={false}` nos `Notice` aninhados: eles já estão dentro desta região viva; um
        `role` próprio aninhado dentro de `aria-live="polite"` tende a duplicar o anúncio.
      */}
      <section aria-live="polite" aria-label="Resultados da busca" className="result-list">
        {searchState.status === 'loading' && <p role="status">Buscando…</p>}

        {searchState.status === 'error' && searchState.error.code === 'embedding_unavailable' && (
          <Notice tone="info" announce={false}>
            <p>
              Esta consulta não tem um vetor pré-computado disponível nesta demonstração — que
              roda de propósito sem chave de provedor de embeddings (spec.md D8). Experimente uma
              das consultas abaixo: são exatamente as que o teste automatizado usa para medir a
              busca.
            </p>
            {searchState.error.exampleQueries && searchState.error.exampleQueries.length > 0 ? (
              <ExampleQueries
                queries={searchState.error.exampleQueries}
                onSelect={handleExampleQueryClick}
              />
            ) : (
              <p>Nenhuma consulta de demonstração está disponível nesta instância ainda.</p>
            )}
          </Notice>
        )}

        {searchState.status === 'error' &&
          (searchState.error.code === 'embedding_provider_error' || searchState.error.code === 'network') && (
            <Notice tone="error" announce={false}>
              {searchState.error.detail}
            </Notice>
          )}

        {searchState.status === 'success' && <ResultList response={searchState.data} />}
      </section>

      <footer>
        <p>
          Estado da API: <StatusIndicator state={healthState} />
        </p>
      </footer>
    </main>
  )
}

export default App
