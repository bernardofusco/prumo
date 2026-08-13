import { useCallback, useEffect, useRef, useState, type MouseEvent } from 'react'

import {
  fetchProfessionalSlots,
  reserveSlot,
  type AgendaError,
  type AgendaProfessionalInfo,
  type AgendaSlotItem,
  type ReserveResponse,
} from '../api/agenda'
import { getClientKey } from '../lib/clientKey'
import { toPath, type View } from '../lib/view'
import { Notice } from './Notice'

export interface ProfessionalSlotsProps {
  readonly slug: string
  /** Mesma navegação de `ResultCard`/`App.tsx` (T13, spec.md D6) — sem `react-router`. */
  readonly onNavigate: (view: View) => void
}

type SlotsState =
  | { readonly status: 'loading' }
  | {
      readonly status: 'success'
      readonly professional: AgendaProfessionalInfo
      readonly timezone: string
      readonly slots: readonly AgendaSlotItem[]
    }
  | { readonly status: 'error'; readonly error: AgendaError }

type ReservationState =
  | { readonly status: 'idle' }
  | { readonly status: 'reserving'; readonly slotId: number }
  | { readonly status: 'success'; readonly data: ReserveResponse }
  | { readonly status: 'error'; readonly error: AgendaError }

/**
 * Rótulo E explicação de cada `status` (spec.md AGN-09/"Contrato API ↔ Frontend": `booked` e
 * `past` "não são reserváveis" — a tela não só desabilita o botão, ela diz por quê ao lado dele,
 * jornada J1/J5). Nenhum destes três valores é calculado aqui — vem pronto de
 * `AgendaSlotItem.status` (api/agenda.ts, já validado contra o vocabulário do contrato).
 */
const SLOT_STATUS_LABEL: Record<AgendaSlotItem['status'], string> = {
  available: 'Disponível',
  booked: 'Já reservado por outra pessoa',
  past: 'Este horário já passou',
}

const DISPLAY_TIME_ZONE_FOR_WINDOW_START = 'America/Sao_Paulo'

/**
 * Início do dia corrente (fuso de exibição, spec.md D7) como instante ISO-8601 com offset
 * explícito — o parâmetro `from` de `GET /api/professionals/{slug}/slots`.
 *
 * **Por que isto existe (ponto de atenção da task T13):** o filtro do servidor é
 * `slot.End > from` e `status: "past"` exige `end <= now` (`SlotAvailability.Classify`, T3) — as
 * duas condições são MUTUAMENTE EXCLUSIVAS na janela default do servidor (`agora..agora+7d`), então
 * nenhum slot `past` aparece nela: um horário em andamento mostra `available` e, ao terminar, some
 * da lista em vez de virar `past` (J5 fica sem exemplo observável). Sem mudar a API — `from`/`to`
 * já são parâmetros do cliente (spec.md "Contrato API ↔ Frontend") — pedimos a janela a partir da
 * meia-noite do dia corrente: um slot cujo fim já passou HOJE ainda cai dentro de `[from, to)` e o
 * servidor o classifica como `past` em vez de omiti-lo.
 *
 * **Por que o fuso é um literal aqui, e isso NÃO contradiz "não hardcode" do Done-when:** aquela
 * regra é sobre EXIBIR horários — sempre com o campo `timezone` da RESPOSTA, nunca um literal do
 * cliente (ver `formatSlotRange` abaixo). Aqui não existe resposta ainda — é o instante que abre a
 * PRIMEIRA requisição. `America/Sao_Paulo` é o mesmo fuso default de `Scheduling:DisplayTimeZone`
 * (design.md §2) e não observa horário de verão desde 2019 (deslocamento `-03:00` estável) — por
 * isso um offset fixo describe o fuso corretamente sem precisar de `Intl.DateTimeFormat` para
 * resolver a hora local a partir de um nome de fuso (o que o `Intl` só faz de fato a partir das
 * PARTES formatadas, não devolve o offset isolado sem mais contas).
 */
function startOfTodayInDisplayTimeZone(now: Date): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: DISPLAY_TIME_ZONE_FOR_WINDOW_START,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now)

  const year = parts.find((part) => part.type === 'year')?.value
  const month = parts.find((part) => part.type === 'month')?.value
  const day = parts.find((part) => part.type === 'day')?.value

  return `${year ?? '1970'}-${month ?? '01'}-${day ?? '01'}T00:00:00-03:00`
}

/**
 * Formata `start`/`end` (ISO-8601 UTC, `api/agenda.ts`) no fuso que a API mandou —
 * NUNCA um literal (Done-when da T13: "Horários exibidos no fuso de Brasília… use-o, não
 * hardcode"). Mesmo dia no fuso de exibição ⇒ só a hora final se repete; dias diferentes ⇒ as duas
 * datas completas, para não sugerir engano num slot que atravessa a meia-noite.
 */
function formatSlotRange(startIso: string, endIso: string, timezone: string): string {
  const start = new Date(startIso)
  const end = new Date(endIso)

  const dateTimeFormatter = new Intl.DateTimeFormat('pt-BR', {
    timeZone: timezone,
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
  const timeFormatter = new Intl.DateTimeFormat('pt-BR', {
    timeZone: timezone,
    hour: '2-digit',
    minute: '2-digit',
  })
  const dateFormatter = new Intl.DateTimeFormat('pt-BR', {
    timeZone: timezone,
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  })

  const sameDay = dateFormatter.format(start) === dateFormatter.format(end)

  return sameDay
    ? `${dateTimeFormatter.format(start)} – ${timeFormatter.format(end)}`
    : `${dateTimeFormatter.format(start)} – ${dateTimeFormatter.format(end)}`
}

/**
 * A tela do profissional (MET-480 T13, design.md §10, spec.md J1/J2/J3/J5/J6, AGN-13/AGN-14):
 * lista os horários com o `status` que a API mandou e reserva um `available`. Regra de ouro
 * herdada (spec.md "Contrato API ↔ Frontend"): a API explica, o React só renderiza — nenhum
 * `if (slot.end < now)` mora aqui.
 */
export function ProfessionalSlots({ slug, onNavigate }: ProfessionalSlotsProps) {
  const [slotsState, setSlotsState] = useState<SlotsState>({ status: 'loading' })
  const [reservationState, setReservationState] = useState<ReservationState>({ status: 'idle' })

  // Mesma defesa dupla de `hooks/useSearch.ts`: aborta a tentativa de reserva anterior e descarta
  // qualquer resposta que chegue depois de uma mais nova ter começado (dois cliques rápidos em
  // slots diferentes nunca pintam a confirmação/erro do slot errado).
  const reserveAbortControllerRef = useRef<AbortController | null>(null)
  const latestReserveRequestIdRef = useRef(0)

  useEffect(() => {
    let cancelled = false
    const controller = new AbortController()

    async function load() {
      setSlotsState({ status: 'loading' })
      setReservationState({ status: 'idle' })

      const from = startOfTodayInDisplayTimeZone(new Date())
      const result = await fetchProfessionalSlots(slug, { from }, controller.signal)

      if (cancelled) {
        return
      }

      setSlotsState(
        result.ok
          ? { status: 'success', professional: result.data.professional, timezone: result.data.timezone, slots: result.data.slots }
          : { status: 'error', error: result.error },
      )
    }

    void load()

    return () => {
      cancelled = true
      controller.abort()
    }
  }, [slug])

  const handleReserve = useCallback((slotId: number) => {
    reserveAbortControllerRef.current?.abort()

    const controller = new AbortController()
    reserveAbortControllerRef.current = controller

    const requestId = latestReserveRequestIdRef.current + 1
    latestReserveRequestIdRef.current = requestId

    setReservationState({ status: 'reserving', slotId })

    const clientKey = getClientKey()

    void reserveSlot(slotId, clientKey, controller.signal).then((result) => {
      if (latestReserveRequestIdRef.current !== requestId) {
        return
      }

      setReservationState(result.ok ? { status: 'success', data: result.data } : { status: 'error', error: result.error })
    })
  }, [])

  useEffect(() => {
    return () => {
      reserveAbortControllerRef.current?.abort()
    }
  }, [])

  function handleBackToSearch(event: MouseEvent<HTMLAnchorElement>) {
    event.preventDefault()
    onNavigate({ kind: 'search' })
  }

  const timezone = slotsState.status === 'success' ? slotsState.timezone : null

  return (
    <section className="professional-slots">
      <p className="professional-slots__back">
        <a href={toPath({ kind: 'search' })} onClick={handleBackToSearch}>
          ← Voltar à busca
        </a>
      </p>

      {slotsState.status === 'loading' && <p role="status">Carregando horários…</p>}

      {slotsState.status === 'error' && <Notice tone="error">{slotsState.error.detail}</Notice>}

      {slotsState.status === 'success' && (
        <>
          <h2>{slotsState.professional.fullName}</h2>
          <p className="professional-slots__meta">{slotsState.professional.specialty}</p>

          {slotsState.slots.length === 0 ? (
            <p>Nenhum horário publicado para os próximos dias.</p>
          ) : (
            <ul className="professional-slots__list" role="list">
              {slotsState.slots.map((slot) => {
                const statusId = `professional-slots__status-${slot.id}`
                const isReservingThisSlot = reservationState.status === 'reserving' && reservationState.slotId === slot.id

                return (
                  <li key={slot.id} className="professional-slots__item">
                    <span className="professional-slots__range">{formatSlotRange(slot.start, slot.end, slotsState.timezone)}</span>
                    <span id={statusId} className="professional-slots__status">
                      {SLOT_STATUS_LABEL[slot.status]}
                    </span>
                    <button
                      type="button"
                      aria-describedby={statusId}
                      disabled={slot.status !== 'available' || reservationState.status === 'reserving'}
                      onClick={() => {
                        handleReserve(slot.id)
                      }}
                    >
                      {isReservingThisSlot ? 'Reservando…' : 'Reservar'}
                    </button>
                  </li>
                )
              })}
            </ul>
          )}
        </>
      )}

      {/*
        Região viva PERSISTENTE (mesma lição da MET-479, App.tsx): montada desde o primeiro render
        deste componente — mesmo vazia, no estado `idle` — e não só quando a reserva termina. Uma
        região `aria-live` que só nasce no DOM já com texto dentro não é confiavelmente anunciada
        por leitor de tela (achado do Reviewer, reaplicado aqui).
      */}
      <section aria-live="polite" aria-label="Resultado da reserva" className="reservation-result">
        {reservationState.status === 'success' && timezone && (
          <Notice tone="info" announce={false}>
            {reservationState.data.replay ? 'Você já tinha reservado este horário.' : 'Horário reservado com sucesso.'}{' '}
            {formatSlotRange(reservationState.data.start, reservationState.data.end, timezone)} com{' '}
            {slotsState.status === 'success' ? slotsState.professional.fullName : reservationState.data.professionalSlug}.
          </Notice>
        )}

        {reservationState.status === 'error' && (
          <Notice tone="error" announce={false}>
            {reservationState.error.detail}
          </Notice>
        )}
      </section>
    </section>
  )
}
