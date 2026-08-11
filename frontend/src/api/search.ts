import { roundCoordinate } from '../lib/geo'

// Mesmo padrão de frontend/src/api/health.ts (M0): caminho relativo em dev (proxy /api do Vite,
// vite.config.ts — NÃO muda aqui), VITE_API_BASE_URL em build. Nenhuma URL hardcoded em componente.
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

// ---- Tipos do contrato (só os campos consumidos — mesma disciplina de api/health.ts) --------------
// Espelham src/Prumo.Api/Search/SearchContracts.cs (os [JsonPropertyName] são a verdade, design.md
// §6, spec.md "Contrato API ↔ Frontend"). `readonly` em tudo: o React só formata e desenha, nunca
// muta o que a API devolveu.

export interface SearchGeoInfo {
  readonly applied: boolean
  readonly radiusKm: number | null
}

export interface SearchEmbeddingInfo {
  readonly mode: string
  readonly model: string | null
}

export interface SearchRankingInfo {
  readonly semanticWeight: number
  readonly proximityWeight: number
  readonly distanceDecayKm: number
  readonly minSemanticScore: number
}

/**
 * `proximity`/`proximityContribution` são `null` — nunca `0` — quando a busca não tem localização
 * (D4 da spec): `0` afirmaria "a proximidade é péssima" sobre um dado que não existe. O frontend não
 * concilia isso com nenhuma conta; só exibe o que vier (ver nota da task sobre `semanticContribution`
 * "bruto" sem localização — assunto de texto da T9, não de aritmética aqui).
 */
export interface SearchScoreFactors {
  readonly semantic: number
  readonly proximity: number | null
  readonly semanticContribution: number
  readonly proximityContribution: number | null
}

export interface SearchResultItem {
  readonly slug: string
  readonly fullName: string
  readonly specialty: string
  readonly city: string
  readonly state: string
  readonly serviceDescription: string
  readonly distanceKm: number | null
  readonly score: number
  readonly factors: SearchScoreFactors
}

export interface SearchResponse {
  readonly query: string
  readonly geo: SearchGeoInfo
  readonly embedding: SearchEmbeddingInfo
  readonly ranking: SearchRankingInfo
  readonly totalCandidates: number
  readonly results: readonly SearchResultItem[]
}

export interface CityOption {
  readonly name: string
  readonly state: string
  readonly latitude: number
  readonly longitude: number
}

export interface SearchOptionsResponse {
  readonly embeddingMode: string
  readonly defaultResultLimit: number
  /**
   * Mesmo `Search:MaxQueryLength` que o servidor aplica em `GET /api/search` (MET-516) — única fonte
   * de verdade; o frontend nunca declara esse número por conta própria, só o usa para limitar o
   * campo e avisar da proximidade do limite antes do 400.
   */
  readonly maxQueryLength: number
  readonly exampleQueries: readonly string[]
  readonly cities: readonly CityOption[]
}

export interface SearchParams {
  readonly q: string
  /** Bruta, do navegador/cidade escolhida — arredondada aqui dentro (roundCoordinate), nunca pelo chamador. */
  readonly lat?: number
  readonly lng?: number
  readonly radiusKm?: number
  readonly limit?: number
}

/**
 * Os quatro códigos que `GET /api/search` pode devolver (spec.md "Contrato API ↔ Frontend") mais
 * `network` — erro de transporte/formato que não veio de um `problem+json` reconhecível. O frontend
 * não inventa um quinto código: tudo que não é um dos três de `problem+json` cai em `network`.
 */
export type SearchErrorCode =
  | 'invalid_request'
  | 'embedding_unavailable'
  | 'embedding_provider_error'
  | 'network'

export interface SearchError {
  readonly code: SearchErrorCode
  readonly detail: string
  /** Só populado quando code === 'embedding_unavailable' (422, design.md §6). */
  readonly exampleQueries?: readonly string[]
}

export type SearchResult =
  | { readonly ok: true; readonly data: SearchResponse }
  | { readonly ok: false; readonly error: SearchError }

export type SearchOptionsResult =
  | { readonly ok: true; readonly data: SearchOptionsResponse }
  | { readonly ok: false; readonly error: SearchError }

// ---- fetchSearch -------------------------------------------------------------------------------

/**
 * `GET /api/search`. Nenhuma exceção escapa daqui — mesma disciplina de `fetchHealth` (api/health.ts,
 * M0): rede fora do ar, resposta não-ok e corpo em formato inesperado viram `SearchResult` com
 * `ok: false`, nunca um `throw`. Cancelamento é responsabilidade do chamador (`useSearch`): esta
 * função só repassa `signal` ao `fetch`.
 *
 * **Garantia de que `hooks/useSearch.ts` depende, sem `.catch` próprio:** a promise devolvida por
 * esta função NUNCA rejeita — todo caminho de erro (rede, abort, HTTP não-ok, JSON inválido, corpo
 * em formato inesperado) resolve com `{ ok: false, error }`. Se esta função um dia passar a
 * `throw`/rejeitar em algum caminho, `useSearch` trava em `loading` com uma unhandled rejection (o
 * `.then(...)` de lá não tem `.catch`) — qualquer mudança aqui que enfraqueça essa garantia precisa
 * também atualizar o consumidor.
 */
export async function fetchSearch(params: SearchParams, signal?: AbortSignal): Promise<SearchResult> {
  try {
    const response = await fetch(buildSearchUrl(params), { signal })

    if (!response.ok) {
      return await parseSearchErrorResponse(response)
    }

    const body: unknown = await response.json()

    if (!isSearchResponseShape(body)) {
      return {
        ok: false,
        error: { code: 'network', detail: 'A API respondeu com um corpo de busca em formato inesperado.' },
      }
    }

    return { ok: true, data: body }
  } catch {
    // Cobre falha de rede (fetch rejeita) E cancelamento (AbortError) — os dois viram o mesmo
    // resultado tipado aqui; quem decide se um cancelamento importa é o `useSearch` (descarte por
    // geração de requisição), não esta função.
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao buscar profissionais.' } }
  }
}

// ---- fetchSearchOptions -------------------------------------------------------------------------

/**
 * `GET /api/search/options`. O contrato (design.md §3.5/§6) não descreve nenhum caminho de erro
 * (sempre 200) — mas rede fora do ar ou um corpo inesperado ainda são possíveis na prática (API
 * fora do ar, proxy quebrado), e viram `SearchResult` com `ok: false, error.code: 'network'`, nunca
 * exceção — mesma disciplina de `fetchSearch`/`fetchHealth`.
 */
export async function fetchSearchOptions(signal?: AbortSignal): Promise<SearchOptionsResult> {
  try {
    const response = await fetch(`${API_BASE_URL}/api/search/options`, { signal })

    if (!response.ok) {
      return {
        ok: false,
        error: { code: 'network', detail: 'Não foi possível carregar as opções de busca.' },
      }
    }

    const body: unknown = await response.json()

    if (!isSearchOptionsResponseShape(body)) {
      return {
        ok: false,
        error: { code: 'network', detail: 'A API respondeu com um corpo de opções em formato inesperado.' },
      }
    }

    return { ok: true, data: body }
  } catch {
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao carregar as opções de busca.' } }
  }
}

// ---- construção da URL (query params) ------------------------------------------------------------

function buildSearchUrl(params: SearchParams): string {
  const query = new URLSearchParams()
  query.set('q', params.q)

  // lat/lng só entram juntos (mesma regra do contrato, spec.md: "um sem o outro ⇒ 400" — este
  // cliente não decide isso, só reflete a mesma condição ao montar a URL) — e SEMPRE arredondados a
  // 3 casas antes de virar parâmetro (lib/geo.ts, minimização de dado).
  if (params.lat !== undefined && params.lng !== undefined) {
    query.set('lat', roundCoordinate(params.lat).toString())
    query.set('lng', roundCoordinate(params.lng).toString())

    // radiusKm só é um parâmetro válido JUNTO de localização — o backend rejeita com 400 caso
    // contrário (SearchEndpoints.cs: "'radiusKm' só é válido quando 'lat' e 'lng' também são
    // informados"). Por isso ele mora DENTRO deste bloco, não fora: sem lat/lng, um radiusKm que o
    // chamador tenha passado é descartado aqui, não vira uma requisição fadada ao 400.
    if (params.radiusKm !== undefined) {
      query.set('radiusKm', params.radiusKm.toString())
    }
  }

  if (params.limit !== undefined) {
    query.set('limit', params.limit.toString())
  }

  return `${API_BASE_URL}/api/search?${query.toString()}`
}

// ---- parsing defensivo do corpo (nenhum `any`, tudo por type guard) -------------------------------

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/**
 * `value` é `number | null` — nunca `undefined` (chave ausente falha aqui de propósito: o contrato
 * sempre inclui a chave, com `null` explícito quando não há localização; uma chave ausente é corpo
 * malformado, não "sem localização").
 */
function isNullableNumber(value: unknown): value is number | null {
  return value === null || typeof value === 'number'
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

/**
 * `factors.*` (design.md §6): é exatamente o que `ResultCard` (T9) vai desreferenciar sem checar de
 * novo (`item.factors.semantic`, `item.factors.proximity` etc.) — por isso valida CADA campo, não só
 * "é um objeto". `proximity`/`proximityContribution` aceitam `null` (D4: sem localização).
 */
function isSearchScoreFactorsShape(value: unknown): value is SearchScoreFactors {
  return (
    isRecord(value) &&
    typeof value.semantic === 'number' &&
    isNullableNumber(value.proximity) &&
    typeof value.semanticContribution === 'number' &&
    isNullableNumber(value.proximityContribution)
  )
}

/** Um item de `results[]` — os campos que `ResultCard` (T9) exibe, `factors` incluído. */
function isSearchResultItemShape(value: unknown): value is SearchResultItem {
  return (
    isRecord(value) &&
    typeof value.slug === 'string' &&
    typeof value.fullName === 'string' &&
    typeof value.specialty === 'string' &&
    typeof value.city === 'string' &&
    typeof value.state === 'string' &&
    typeof value.serviceDescription === 'string' &&
    isNullableNumber(value.distanceKm) &&
    typeof value.score === 'number' &&
    isSearchScoreFactorsShape(value.factors)
  )
}

function isSearchGeoInfoShape(value: unknown): value is SearchGeoInfo {
  return isRecord(value) && typeof value.applied === 'boolean' && isNullableNumber(value.radiusKm)
}

function isSearchEmbeddingInfoShape(value: unknown): value is SearchEmbeddingInfo {
  return isRecord(value) && typeof value.mode === 'string' && isNullableString(value.model)
}

/** `ranking.*` (design.md §6): é o que a T9 lê para legendar "relevância × peso" sem fazer conta. */
function isSearchRankingInfoShape(value: unknown): value is SearchRankingInfo {
  return (
    isRecord(value) &&
    typeof value.semanticWeight === 'number' &&
    typeof value.proximityWeight === 'number' &&
    typeof value.distanceDecayKm === 'number' &&
    typeof value.minSemanticScore === 'number'
  )
}

/**
 * Validação PROFUNDA, não só das 6 chaves de topo: um corpo com `results[0].factors: null` ou
 * `ranking: {}` retornava `ok:true` na versão rasa deste guard (achado do review da T8) — e a T9
 * desreferencia `item.factors.semantic`/`ranking.semanticWeight` sem checar de novo, então o buraco
 * vira tela branca em vez de estado de erro. Cada campo que ALGUÉM lê é validado; nenhum campo que
 * ninguém lê precisa ser.
 */
function isSearchResponseShape(value: unknown): value is SearchResponse {
  return (
    isRecord(value) &&
    typeof value.query === 'string' &&
    typeof value.totalCandidates === 'number' &&
    Array.isArray(value.results) &&
    value.results.every(isSearchResultItemShape) &&
    isSearchGeoInfoShape(value.geo) &&
    isSearchEmbeddingInfoShape(value.embedding) &&
    isSearchRankingInfoShape(value.ranking)
  )
}

function isCityOptionShape(value: unknown): value is CityOption {
  return (
    isRecord(value) &&
    typeof value.name === 'string' &&
    typeof value.state === 'string' &&
    typeof value.latitude === 'number' &&
    typeof value.longitude === 'number'
  )
}

function isSearchOptionsResponseShape(value: unknown): value is SearchOptionsResponse {
  return (
    isRecord(value) &&
    typeof value.embeddingMode === 'string' &&
    typeof value.defaultResultLimit === 'number' &&
    typeof value.maxQueryLength === 'number' &&
    Array.isArray(value.exampleQueries) &&
    value.exampleQueries.every((item): item is string => typeof item === 'string') &&
    Array.isArray(value.cities) &&
    value.cities.every(isCityOptionShape)
  )
}

const KNOWN_PROBLEM_ERROR_CODES: readonly SearchErrorCode[] = [
  'invalid_request',
  'embedding_unavailable',
  'embedding_provider_error',
]

function toKnownErrorCode(code: unknown): SearchErrorCode | null {
  return typeof code === 'string' && (KNOWN_PROBLEM_ERROR_CODES as readonly string[]).includes(code)
    ? (code as SearchErrorCode)
    : null
}

function toStringArrayOrUndefined(value: unknown): readonly string[] | undefined {
  return Array.isArray(value) && value.every((item): item is string => typeof item === 'string')
    ? value
    : undefined
}

/**
 * Resposta não-ok de `GET /api/search`: sempre `application/problem+json` no contrato (400/422/502,
 * design.md §6), mas esta função não confia nisso cegamente — um `code` ausente/desconhecido, ou um
 * corpo que não é nem JSON, vira `network` em vez de propagar um código inventado.
 */
async function parseSearchErrorResponse(response: Response): Promise<SearchResult> {
  let body: unknown
  try {
    body = await response.json()
  } catch {
    return { ok: false, error: { code: 'network', detail: 'A API respondeu com um erro sem corpo interpretável.' } }
  }

  if (!isRecord(body)) {
    return { ok: false, error: { code: 'network', detail: 'A API respondeu com um corpo de erro em formato inesperado.' } }
  }

  const code = toKnownErrorCode(body.code)

  if (code === null) {
    return {
      ok: false,
      error: {
        code: 'network',
        detail: typeof body.detail === 'string' ? body.detail : 'A API respondeu com um erro não reconhecido.',
      },
    }
  }

  return {
    ok: false,
    error: {
      code,
      detail: typeof body.detail === 'string' ? body.detail : 'A API respondeu com um erro.',
      exampleQueries: code === 'embedding_unavailable' ? toStringArrayOrUndefined(body.exampleQueries) : undefined,
    },
  }
}
