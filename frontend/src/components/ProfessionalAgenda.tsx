import { useEffect, useRef, useState, type FormEvent, type MouseEvent } from 'react'

import {
  deleteSlot,
  fetchProfessionalSlots,
  publishSlot,
  type AgendaError,
  type AgendaProfessionalInfo,
  type AgendaSlotItem,
} from '../api/agenda'
import { toPath, type View } from '../lib/view'
import { Notice } from './Notice'

export interface ProfessionalAgendaProps {
  readonly slug: string
  /** Mesma navegação de `ProfessionalSlots`/`App.tsx` (T12/T13) — sem `react-router` (spec.md D6). */
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

type ActionState =
  | { readonly status: 'idle' }
  | { readonly status: 'pending' }
  | { readonly status: 'published'; readonly slot: AgendaSlotItem }
  | { readonly status: 'deleted' }
  | { readonly status: 'error'; readonly error: AgendaError }

/**
 * Rótulo de cada `status` ao lado de cada horário publicado — mesmo vocabulário de
 * `ProfessionalSlots.SLOT_STATUS_LABEL` (T13), duplicado de propósito: telas com papéis diferentes
 * (cliente reserva × profissional publica/remove), mesmo molde de duplicação já usado no backend
 * (`ListSlotsRequestValidator.TryParseInstant`/`PublishSlotRequestValidator.TryParseInstant`,
 * `AgendaEndpoints.cs`: "mesmo molde... duplicado aqui, não compartilhado").
 */
const SLOT_STATUS_LABEL: Record<AgendaSlotItem['status'], string> = {
  available: 'Disponível',
  booked: 'Reservado por um cliente',
  past: 'Já passou',
}

/**
 * Formata `start`/`end` (ISO-8601 UTC) no fuso que a API mandou (`timezone` da resposta) — NUNCA um
 * literal do cliente. Mesma disciplina de `ProfessionalSlots.formatSlotRange` (T13), duplicada aqui
 * de propósito (ver comentário de `SLOT_STATUS_LABEL` acima).
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
  const timeFormatter = new Intl.DateTimeFormat('pt-BR', { timeZone: timezone, hour: '2-digit', minute: '2-digit' })
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
 * Converte o valor de um campo `<input type="datetime-local">` (hora LOCAL do navegador, sem fuso —
 * `"AAAA-MM-DDTHH:mm"`) para ISO-8601 UTC. `new Date(...)` já interpreta essa forma como hora local
 * (ECMA-262 21.4.3.2, "Date Time String Format" sem designador de fuso explícito) — não é um fuso
 * "chutado" aqui dentro, é o PRÓPRIO navegador aplicando o fuso que ele já sabe, o mesmo que o
 * profissional viu no seletor do campo. `null` ⇒ campo vazio ou valor não interpretável.
 */
function localDateTimeToIsoUtc(rawValue: string): string | null {
  if (rawValue.trim().length === 0) {
    return null
  }

  const parsed = new Date(rawValue)

  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString()
}

/**
 * A agenda do profissional (MET-480 T14, design.md §10, spec.md D2/J4, AGN-10/AGN-14): publica e
 * remove horários o bastante para a demo — **sem autenticação nenhuma**. Qualquer visitante que
 * abra esta URL age como se fosse o profissional do `slug` (spec.md D2, jornada J4): é uma decisão
 * DELIBERADA de escopo de demo, não um descuido, e por isso o aviso abaixo é PERMANENTE (nunca um
 * toast que some sozinho, nunca escondido) — quem abrir esta tela precisa entender isso antes de
 * publicar ou remover qualquer coisa.
 *
 * Regra de ouro herdada (spec.md "Contrato API ↔ Frontend", mesma de `ProfessionalSlots`): a API
 * explica, o React só renderiza. `overlap` (409 `slot_overlap`) e `delete` bloqueado (409
 * `slot_has_reservation`) mostram o `detail` que a API mandou — nunca um texto inventado aqui, e
 * nunca o texto de 503 (`service_unavailable`, que também tem seu próprio `detail` distinto).
 */
export function ProfessionalAgenda({ slug, onNavigate }: ProfessionalAgendaProps) {
  const [slotsState, setSlotsState] = useState<SlotsState>({ status: 'loading' })
  const [actionState, setActionState] = useState<ActionState>({ status: 'idle' })
  const [startValue, setStartValue] = useState('')
  const [endValue, setEndValue] = useState('')
  const [formError, setFormError] = useState<string | null>(null)

  // Só um controller: publicar e remover nunca acontecem ao mesmo tempo nesta tela (os botões ficam
  // desabilitados enquanto `actionState.status === 'pending'`) — o único papel dele é abortar uma
  // requisição em voo se o componente desmontar (troca de tela) antes da resposta chegar.
  const actionAbortControllerRef = useRef<AbortController | null>(null)

  useEffect(() => {
    let cancelled = false
    const controller = new AbortController()

    async function load() {
      setSlotsState({ status: 'loading' })

      const result = await fetchProfessionalSlots(slug, {}, controller.signal)

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

  useEffect(() => {
    return () => {
      actionAbortControllerRef.current?.abort()
    }
  }, [])

  function handlePublish(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    const start = localDateTimeToIsoUtc(startValue)
    const end = localDateTimeToIsoUtc(endValue)

    if (start === null || end === null) {
      setFormError('Informe início e fim válidos para publicar o horário.')
      return
    }

    setFormError(null)

    actionAbortControllerRef.current?.abort()
    const controller = new AbortController()
    actionAbortControllerRef.current = controller

    setActionState({ status: 'pending' })

    void publishSlot(slug, start, end, controller.signal).then((result) => {
      if (!result.ok) {
        setActionState({ status: 'error', error: result.error })
        return
      }

      // Otimista a partir da PRÓPRIA resposta da API (não um refetch): o novo horário pode cair fora
      // da janela default (agora..agora+7d) de um novo GET se o profissional publicar bem à frente —
      // a API já devolveu o `AgendaSlotItem` completo, então a lista se atualiza sem depender disso.
      setSlotsState((current) =>
        current.status === 'success'
          ? { ...current, slots: [...current.slots, result.data].sort((a, b) => a.start.localeCompare(b.start)) }
          : current,
      )
      setStartValue('')
      setEndValue('')
      setActionState({ status: 'published', slot: result.data })
    })
  }

  function handleDelete(slotId: number) {
    actionAbortControllerRef.current?.abort()
    const controller = new AbortController()
    actionAbortControllerRef.current = controller

    setActionState({ status: 'pending' })

    void deleteSlot(slug, slotId, controller.signal).then((result) => {
      if (!result.ok) {
        setActionState({ status: 'error', error: result.error })
        return
      }

      setSlotsState((current) =>
        current.status === 'success' ? { ...current, slots: current.slots.filter((slot) => slot.id !== slotId) } : current,
      )
      setActionState({ status: 'deleted' })
    })
  }

  function handleBackToSearch(event: MouseEvent<HTMLAnchorElement>) {
    event.preventDefault()
    onNavigate({ kind: 'search' })
  }

  const timezone = slotsState.status === 'success' ? slotsState.timezone : null
  const isActionPending = actionState.status === 'pending'

  return (
    <section className="professional-agenda">
      <p className="professional-agenda__back">
        <a href={toPath({ kind: 'search' })} onClick={handleBackToSearch}>
          ← Voltar à busca
        </a>
      </p>

      {/*
        Aviso PERMANENTE (spec.md D2, tasks.md T14 "Done when"): sempre visível, sem timeout, sem
        botão de fechar — renderizado ANTES de qualquer estado de carregamento/erro/formulário, para
        que apareça mesmo se a lista de horários falhar ao carregar.
      */}
      <Notice tone="warning">
        <p>
          <strong>Demonstração sem autenticação.</strong> Esta tela não pede login: qualquer pessoa que tenha este
          endereço pode publicar ou remover horários da agenda de <strong>{slug}</strong>. Numa versão real isto
          exigiria identidade verificada do profissional — aqui é uma decisão deliberada de escopo de demo (spec.md
          D2), não um descuido.
        </p>
      </Notice>

      {slotsState.status === 'loading' && <p role="status">Carregando agenda…</p>}

      {slotsState.status === 'error' && <Notice tone="error">{slotsState.error.detail}</Notice>}

      {slotsState.status === 'success' && (
        <>
          <h2>{slotsState.professional.fullName}</h2>
          <p className="professional-agenda__meta">{slotsState.professional.specialty}</p>

          <form className="professional-agenda__form" aria-label="Publicar horário" onSubmit={handlePublish}>
            <div className="professional-agenda__field">
              <label htmlFor="professional-agenda-start">Início</label>
              <input
                id="professional-agenda-start"
                name="start"
                type="datetime-local"
                required
                value={startValue}
                onChange={(event) => {
                  setStartValue(event.target.value)
                }}
              />
            </div>

            <div className="professional-agenda__field">
              <label htmlFor="professional-agenda-end">Fim</label>
              <input
                id="professional-agenda-end"
                name="end"
                type="datetime-local"
                required
                value={endValue}
                onChange={(event) => {
                  setEndValue(event.target.value)
                }}
              />
            </div>

            {formError && (
              <p role="alert" className="professional-agenda__form-error">
                {formError}
              </p>
            )}

            <button type="submit" disabled={isActionPending}>
              {isActionPending ? 'Publicando…' : 'Publicar horário'}
            </button>
          </form>

          {slotsState.slots.length === 0 ? (
            <p>Nenhum horário publicado ainda.</p>
          ) : (
            <ul className="professional-agenda__list" role="list">
              {slotsState.slots.map((slot) => (
                <li key={slot.id} className="professional-agenda__item">
                  <span className="professional-agenda__range">
                    {formatSlotRange(slot.start, slot.end, slotsState.timezone)}
                  </span>
                  <span className="professional-agenda__status">{SLOT_STATUS_LABEL[slot.status]}</span>
                  <button
                    type="button"
                    disabled={isActionPending}
                    onClick={() => {
                      handleDelete(slot.id)
                    }}
                  >
                    Remover
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}

      {/*
        Região viva PERSISTENTE (mesma lição da MET-479/T13, `ProfessionalSlots.tsx`): montada desde
        o primeiro render deste componente, mesmo vazia — nunca só quando a publicação/remoção
        termina. `announce={false}` nos `Notice` aninhados: já estão dentro desta região viva própria.
      */}
      <section aria-live="polite" aria-label="Resultado da agenda" className="professional-agenda__result">
        {actionState.status === 'published' && timezone && (
          <Notice tone="info" announce={false}>
            Horário publicado: {formatSlotRange(actionState.slot.start, actionState.slot.end, timezone)}.
          </Notice>
        )}

        {actionState.status === 'deleted' && (
          <Notice tone="info" announce={false}>
            Horário removido da agenda.
          </Notice>
        )}

        {actionState.status === 'error' && (
          <Notice tone="error" announce={false}>
            {actionState.error.detail}
          </Notice>
        )}
      </section>
    </section>
  )
}
