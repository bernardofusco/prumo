import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { AgendaSlotItem, ListSlotsResult, ReserveResult } from '../api/agenda'
import { fetchProfessionalSlots, reserveSlot } from '../api/agenda'
import { ProfessionalSlots } from './ProfessionalSlots'

vi.mock('../api/agenda', () => ({
  fetchProfessionalSlots: vi.fn(),
  reserveSlot: vi.fn(),
}))

// `getClientKey` fixo (não o `localStorage` de verdade): esta suíte testa a TELA, não a
// identidade — `lib/clientKey.test.ts` já cobre `getClientKey` isoladamente.
vi.mock('../lib/clientKey', () => ({
  getClientKey: () => '11111111-1111-4111-8111-111111111111',
}))

const fetchProfessionalSlotsMock = vi.mocked(fetchProfessionalSlots)
const reserveSlotMock = vi.mocked(reserveSlot)

const PROFESSIONAL = { slug: 'ana-ribeiro-bh-01', fullName: 'Ana Ribeiro', specialty: 'Encanador' }
const TIMEZONE = 'America/Sao_Paulo'

// 12:00Z/13:00Z de um dia de outubro caem no MESMO dia em America/Sao_Paulo (UTC-3: 09:00–10:00) —
// fixture escolhida para não atravessar meia-noite e manter a asserção de horário sem ambiguidade.
const AVAILABLE_SLOT: AgendaSlotItem = { id: 1, start: '2026-10-03T12:00:00Z', end: '2026-10-03T13:00:00Z', status: 'available' }
const BOOKED_SLOT: AgendaSlotItem = { id: 2, start: '2026-10-03T14:00:00Z', end: '2026-10-03T15:00:00Z', status: 'booked' }
const PAST_SLOT: AgendaSlotItem = { id: 3, start: '2026-10-03T08:00:00Z', end: '2026-10-03T09:00:00Z', status: 'past' }

function slotsSuccess(slots: readonly AgendaSlotItem[]): ListSlotsResult {
  return { ok: true, data: { professional: PROFESSIONAL, timezone: TIMEZONE, slots } }
}

function reserveSuccess(overrides: Partial<{ replay: boolean }> = {}): ReserveResult {
  return {
    ok: true,
    data: {
      reservationId: 7,
      slotId: AVAILABLE_SLOT.id,
      professionalSlug: PROFESSIONAL.slug,
      start: AVAILABLE_SLOT.start,
      end: AVAILABLE_SLOT.end,
      replay: overrides.replay ?? false,
    },
  }
}

describe('ProfessionalSlots', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-13T15:00:00Z')) // 12:00 em America/Sao_Paulo, 13/08/2026
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.clearAllMocks()
  })

  it('pede a janela a partir da meia-noite do dia corrente (fuso de exibição) — ponto de atenção da T13 (jornada J5)', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(fetchProfessionalSlotsMock).toHaveBeenCalled()
    })

    const [slug, windowParam] = fetchProfessionalSlotsMock.mock.calls[0]
    expect(slug).toBe('ana-ribeiro-bh-01')
    // 13/08/2026 00:00 em America/Sao_Paulo (UTC-3, sem horário de verão desde 2019).
    expect(windowParam).toEqual({ from: '2026-08-13T00:00:00-03:00' })
  })

  it('mostra "Carregando horários…" e depois o profissional e a especialidade', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    expect(screen.getByText('Carregando horários…')).toBeDefined()

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Ana Ribeiro' })).toBeDefined()
    })
    expect(screen.getByText('Encanador')).toBeDefined()
  })

  it('erro ao carregar a agenda: mostra o detail que a API devolveu', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue({ ok: false, error: { code: 'not_found', detail: 'Nenhum profissional foi encontrado.' } })

    render(<ProfessionalSlots slug="slug-desconhecido" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByText('Nenhum profissional foi encontrado.')).toBeDefined()
    })
  })

  it('slot "available": botão Reservar habilitado', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Reservar' }).disabled).toBe(false)
    })
    expect(screen.getByText('Disponível')).toBeDefined()
  })

  it('slot "booked": botão desabilitado e a razão explicada ao lado (não só o botão cinza)', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([BOOKED_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Reservar' }).disabled).toBe(true)
    })
    expect(screen.getByText('Já reservado por outra pessoa')).toBeDefined()
  })

  // Cobertura explícita pedida pela task (J5): um slot "past" VINDO DA API (nenhum cálculo no
  // React) renderiza desabilitado e explicado — não um `if (slot.end < now)` no componente.
  it('slot "past" vindo da API renderiza desabilitado e explicado (J5) — sem recálculo de passado no React', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([PAST_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Reservar' }).disabled).toBe(true)
    })
    expect(screen.getByText('Este horário já passou')).toBeDefined()
  })

  it('a região aria-live do resultado da reserva já existe (vazia) ANTES de qualquer reserva', () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    // Sem esperar por nada: a região precisa estar no DOM já no primeiro render, antes da lista
    // de horários carregar e antes de qualquer clique em "Reservar" (mesma lição da MET-479).
    const region = screen.getByRole('region', { name: 'Resultado da reserva' })
    expect(region.getAttribute('aria-live')).toBe('polite')
    expect(region.textContent).toBe('')
  })

  it('201 (reserva nova): mostra confirmação com horário e profissional — não como erro', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    reserveSlotMock.mockResolvedValue(reserveSuccess({ replay: false }))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Reservar' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Reservar' }))

    const region = screen.getByRole('region', { name: 'Resultado da reserva' })
    await waitFor(() => {
      expect(within(region).getByText(/Horário reservado com sucesso\./)).toBeDefined()
    })
    // `within(region)`: "Ana Ribeiro" também aparece no <h2> da tela — escopar evita ambiguidade,
    // não é sobre achar o texto em lugar nenhum do documento.
    expect(region.textContent).toContain('Ana Ribeiro')
    expect(reserveSlotMock).toHaveBeenCalledWith(AVAILABLE_SLOT.id, '11111111-1111-4111-8111-111111111111', expect.anything())
  })

  it('200 replay (mesmo cliente): mostra confirmação — replay NÃO é apresentado como erro (J3)', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    reserveSlotMock.mockResolvedValue(reserveSuccess({ replay: true }))

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Reservar' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Reservar' }))

    await waitFor(() => {
      expect(screen.getByText('Você já tinha reservado este horário.', { exact: false })).toBeDefined()
    })
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('409 (slot_conflict): mostra o detail da API — NÃO o texto de 503 ("tente novamente em instantes")', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    reserveSlotMock.mockResolvedValue({
      ok: false,
      error: { code: 'slot_conflict', detail: 'Este horário acabou de ser reservado por outro cliente. Escolha outro horário.' },
    })

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Reservar' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Reservar' }))

    await waitFor(() => {
      expect(screen.getByText('Este horário acabou de ser reservado por outro cliente. Escolha outro horário.')).toBeDefined()
    })
    expect(screen.queryByText(/tente novamente em instantes/)).toBeNull()
  })

  it('422 (slot_not_bookable): mostra o detail da API', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    reserveSlotMock.mockResolvedValue({
      ok: false,
      error: { code: 'slot_not_bookable', detail: 'Este horário já passou e não pode mais ser reservado.' },
    })

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Reservar' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Reservar' }))

    await waitFor(() => {
      expect(screen.getByText('Este horário já passou e não pode mais ser reservado.')).toBeDefined()
    })
  })

  it('503 (service_unavailable, AGN-12/handler genérico): mostra o detail — não confundido com slot_conflict', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    reserveSlotMock.mockResolvedValue({
      ok: false,
      error: {
        code: 'service_unavailable',
        detail: 'Não foi possível completar a operação porque o banco de dados está indisponível no momento. Tente novamente em instantes.',
      },
    })

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Reservar' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Reservar' }))

    await waitFor(() => {
      expect(screen.getByText(/Tente novamente em instantes/)).toBeDefined()
    })
  })

  it('"Voltar à busca" aciona onNavigate para a view de busca, sem recarregar a página', () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
    const onNavigate = vi.fn()

    render(<ProfessionalSlots slug="ana-ribeiro-bh-01" onNavigate={onNavigate} />)

    fireEvent.click(screen.getByRole('link', { name: '← Voltar à busca' }))

    expect(onNavigate).toHaveBeenCalledWith({ kind: 'search' })
  })
})
