/**
 * Cliente HTTP da agenda (MET-480 T12, design.md §10, spec.md "Contrato API ↔ Frontend"). Mesmo
 * padrão de `api/search.ts` (M1): nenhuma exceção escapa destas funções — rede fora do ar, resposta
 * não-ok e corpo em formato inesperado sempre viram `{ ok: false, error }`, nunca um `throw`; os
 * tipos espelham `src/Prumo.Api/Agenda/AgendaContracts.cs` (os `[JsonPropertyName]` de lá são a
 * verdade, não a convenção padrão de serialização).
 *
 * Regra de ouro herdada (spec.md "Contrato API ↔ Frontend"): a API explica, o React renderiza. Nada
 * aqui recalcula `status`, decide conflito ou escolhe defesa — cada função só transporta e tipa o
 * que o servidor já decidiu.
 */

// Em dev, caminho relativo (proxy /api do Vite); em build, VITE_API_BASE_URL — mesmo padrão de
// api/search.ts/api/health.ts. Nenhuma URL hardcoded em componente.
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

/**
 * Cabeçalho de identidade do cliente (spec.md D2): mesmo literal de
 * `Prumo.Api.Agenda.AgendaEndpoints.ClientKeyHeaderName` — duplicado aqui de propósito (o frontend
 * não importa código do backend), não inventado.
 */
export const CLIENT_KEY_HEADER_NAME = 'X-Prumo-Client-Key'

// ---- Tipos do contrato (só os campos consumidos — mesma disciplina de api/search.ts) --------------

export interface AgendaProfessionalInfo {
  readonly slug: string
  readonly fullName: string
  readonly specialty: string
}

/**
 * Espelha `Prumo.Api.Agenda.Scheduling.SlotStatus`/`AgendaSlotItem.Status` (design.md §5/§8): SEMPRE
 * calculado pelo servidor. Nenhum componente desta feature deriva isto de `start`/`end` — ver a nota
 * "Ponto de atenção" da task (`if (slot.end < now)` é o lugar errado).
 */
export type AgendaSlotStatus = 'available' | 'booked' | 'past'

export interface AgendaSlotItem {
  readonly id: number
  /** ISO-8601 UTC (sufixo `Z`) — mesma forma literal do exemplo em spec.md. String, não `Date`: quem formata em `America/Sao_Paulo` é a tela (T13/T14), não este cliente. */
  readonly start: string
  readonly end: string
  readonly status: AgendaSlotStatus
}

export interface ListSlotsResponse {
  readonly professional: AgendaProfessionalInfo
  readonly timezone: string
  readonly slots: readonly AgendaSlotItem[]
}

export interface ReserveResponse {
  readonly reservationId: number
  readonly slotId: number
  readonly professionalSlug: string
  readonly start: string
  readonly end: string
  /** `false` ⇒ `201` (reserva nova). `true` ⇒ `200` (mesmo cliente + mesmo slot já existia, spec.md F3). */
  readonly replay: boolean
}

/**
 * União de TODOS os `code` de `problem+json` documentados em spec.md "Contrato API ↔ Frontend" para
 * as quatro rotas desta feature, mais `network` — o mesmo "quinto código" de `api/search.ts` para
 * erro de transporte/formato que não veio de um `problem+json` reconhecível. Cada função abaixo só
 * produz o subconjunto que a rota dela realmente documenta; nenhuma função inventa um `code` fora
 * desta lista.
 */
export type AgendaErrorCode =
  | 'invalid_request'
  | 'not_found'
  | 'slot_conflict'
  | 'slot_not_bookable'
  | 'slot_overlap'
  | 'slot_has_reservation'
  | 'service_unavailable'
  | 'network'

export interface AgendaError {
  readonly code: AgendaErrorCode
  readonly detail: string
}

type AgendaFailure = { readonly ok: false; readonly error: AgendaError }

export type AgendaResult<T> = { readonly ok: true; readonly data: T } | AgendaFailure

export type ListSlotsResult = AgendaResult<ListSlotsResponse>
export type ReserveResult = AgendaResult<ReserveResponse>
export type PublishSlotResult = AgendaResult<AgendaSlotItem>
export type DeleteSlotResult = { readonly ok: true } | AgendaFailure

// ---- GET /api/professionals/{slug}/slots -----------------------------------------------------------

export interface SlotsWindowParams {
  /** ISO-8601. Ausente ⇒ a API usa "agora" (spec.md "Contrato API ↔ Frontend"). */
  readonly from?: string
  /** ISO-8601. Ausente ⇒ a API usa `from` + `DefaultWindowDays`. */
  readonly to?: string
}

/**
 * `GET /api/professionals/{slug}/slots` (design.md §8, tasks.md T9). Nenhuma exceção escapa — mesma
 * disciplina de `fetchSearch`.
 */
export async function fetchProfessionalSlots(
  slug: string,
  window: SlotsWindowParams = {},
  signal?: AbortSignal,
): Promise<ListSlotsResult> {
  try {
    const response = await fetch(buildSlotsUrl(slug, window), { signal })

    if (!response.ok) {
      return await parseAgendaErrorResponse(response)
    }

    const body: unknown = await response.json()

    if (!isListSlotsResponseShape(body)) {
      return {
        ok: false,
        error: { code: 'network', detail: 'A API respondeu com um corpo de agenda em formato inesperado.' },
      }
    }

    return { ok: true, data: body }
  } catch {
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao carregar a agenda do profissional.' } }
  }
}

function buildSlotsUrl(slug: string, window: SlotsWindowParams): string {
  const query = new URLSearchParams()

  if (window.from !== undefined) {
    query.set('from', window.from)
  }

  if (window.to !== undefined) {
    query.set('to', window.to)
  }

  const queryString = query.toString()
  const suffix = queryString.length > 0 ? `?${queryString}` : ''

  return `${API_BASE_URL}/api/professionals/${encodeURIComponent(slug)}/slots${suffix}`
}

// ---- POST /api/reservations ------------------------------------------------------------------------

/**
 * `POST /api/reservations` (design.md §8, tasks.md T10, spec.md D2/D5/F1-F4/AGN-13): envia o
 * `clientKey` (lib/clientKey.ts) no cabeçalho {@link CLIENT_KEY_HEADER_NAME} — nunca no corpo, nunca
 * derivado aqui dentro (quem chama decide QUANDO chamar `getClientKey()`, esta função só transporta).
 * `201` e `200` (replay) chegam pelo MESMO `ok: true` — `data.replay` já diz qual foi; quem chama
 * (T13) decide como apresentar cada um, este cliente não filtra.
 */
export async function reserveSlot(slotId: number, clientKey: string, signal?: AbortSignal): Promise<ReserveResult> {
  try {
    const response = await fetch(`${API_BASE_URL}/api/reservations`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        [CLIENT_KEY_HEADER_NAME]: clientKey,
      },
      body: JSON.stringify({ slotId }),
      signal,
    })

    if (!response.ok) {
      return await parseAgendaErrorResponse(response)
    }

    const body: unknown = await response.json()

    if (!isReserveResponseShape(body)) {
      return {
        ok: false,
        error: { code: 'network', detail: 'A API respondeu com um corpo de reserva em formato inesperado.' },
      }
    }

    return { ok: true, data: body }
  } catch {
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao reservar o horário.' } }
  }
}

// ---- POST/DELETE /api/professionals/{slug}/slots[/{slotId}] -----------------------------------------

/**
 * `POST /api/professionals/{slug}/slots` (design.md §8, tasks.md T11, spec.md D2/D3): publica um
 * horário na agenda do `slug` da URL — sem autenticação (spec.md D2, demo). `start`/`end` são
 * ISO-8601 já formados por quem chama; esta função não valida duração/futuro (a API decide — 400/422).
 */
export async function publishSlot(
  slug: string,
  start: string,
  end: string,
  signal?: AbortSignal,
): Promise<PublishSlotResult> {
  try {
    const response = await fetch(`${API_BASE_URL}/api/professionals/${encodeURIComponent(slug)}/slots`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ start, end }),
      signal,
    })

    if (!response.ok) {
      return await parseAgendaErrorResponse(response)
    }

    const body: unknown = await response.json()

    if (!isAgendaSlotItemShape(body)) {
      return {
        ok: false,
        error: { code: 'network', detail: 'A API respondeu com um corpo de horário em formato inesperado.' },
      }
    }

    return { ok: true, data: body }
  } catch {
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao publicar o horário.' } }
  }
}

/**
 * `DELETE /api/professionals/{slug}/slots/{slotId}` (design.md §8, tasks.md T11). `204` não tem
 * corpo — o sucesso é só `{ ok: true }`, sem `data` (mesmo molde de `AgendaResult`, sem o campo).
 */
export async function deleteSlot(slug: string, slotId: number, signal?: AbortSignal): Promise<DeleteSlotResult> {
  try {
    const response = await fetch(`${API_BASE_URL}/api/professionals/${encodeURIComponent(slug)}/slots/${slotId}`, {
      method: 'DELETE',
      signal,
    })

    if (!response.ok) {
      return await parseAgendaErrorResponse(response)
    }

    return { ok: true }
  } catch {
    return { ok: false, error: { code: 'network', detail: 'Falha de rede ao remover o horário.' } }
  }
}

// ---- parsing defensivo do corpo (nenhum `any`, tudo por type guard — mesma disciplina de api/search.ts) ----

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isAgendaSlotStatus(value: unknown): value is AgendaSlotStatus {
  return value === 'available' || value === 'booked' || value === 'past'
}

function isAgendaSlotItemShape(value: unknown): value is AgendaSlotItem {
  return (
    isRecord(value) &&
    typeof value.id === 'number' &&
    typeof value.start === 'string' &&
    typeof value.end === 'string' &&
    isAgendaSlotStatus(value.status)
  )
}

function isAgendaProfessionalInfoShape(value: unknown): value is AgendaProfessionalInfo {
  return (
    isRecord(value) &&
    typeof value.slug === 'string' &&
    typeof value.fullName === 'string' &&
    typeof value.specialty === 'string'
  )
}

function isListSlotsResponseShape(value: unknown): value is ListSlotsResponse {
  return (
    isRecord(value) &&
    isAgendaProfessionalInfoShape(value.professional) &&
    typeof value.timezone === 'string' &&
    Array.isArray(value.slots) &&
    value.slots.every(isAgendaSlotItemShape)
  )
}

function isReserveResponseShape(value: unknown): value is ReserveResponse {
  return (
    isRecord(value) &&
    typeof value.reservationId === 'number' &&
    typeof value.slotId === 'number' &&
    typeof value.professionalSlug === 'string' &&
    typeof value.start === 'string' &&
    typeof value.end === 'string' &&
    typeof value.replay === 'boolean'
  )
}

const KNOWN_AGENDA_ERROR_CODES: readonly AgendaErrorCode[] = [
  'invalid_request',
  'not_found',
  'slot_conflict',
  'slot_not_bookable',
  'slot_overlap',
  'slot_has_reservation',
  'service_unavailable',
]

function toKnownAgendaErrorCode(code: unknown): AgendaErrorCode | null {
  return typeof code === 'string' && (KNOWN_AGENDA_ERROR_CODES as readonly string[]).includes(code)
    ? (code as AgendaErrorCode)
    : null
}

/**
 * Resposta não-ok de qualquer rota desta feature: sempre `application/problem+json` no contrato,
 * mas esta função não confia cegamente — um `code` ausente/desconhecido, ou um corpo que não é nem
 * JSON, vira `network` em vez de propagar um código inventado (mesma disciplina de
 * `parseSearchErrorResponse`).
 */
async function parseAgendaErrorResponse(response: Response): Promise<AgendaFailure> {
  let body: unknown

  try {
    body = await response.json()
  } catch {
    return { ok: false, error: { code: 'network', detail: 'A API respondeu com um erro sem corpo interpretável.' } }
  }

  if (!isRecord(body)) {
    return { ok: false, error: { code: 'network', detail: 'A API respondeu com um corpo de erro em formato inesperado.' } }
  }

  const code = toKnownAgendaErrorCode(body.code)

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
    },
  }
}
