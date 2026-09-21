import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { AgendaSlotItem, DeleteSlotResult, ListSlotsResult, PublishSlotResult } from '../api/agenda'
import { deleteSlot, fetchProfessionalSlots, publishSlot } from '../api/agenda'
import { ProfessionalAgenda } from './ProfessionalAgenda'

vi.mock('../api/agenda', () => ({
  fetchProfessionalSlots: vi.fn(),
  publishSlot: vi.fn(),
  deleteSlot: vi.fn(),
}))

const fetchProfessionalSlotsMock = vi.mocked(fetchProfessionalSlots)
const publishSlotMock = vi.mocked(publishSlot)
const deleteSlotMock = vi.mocked(deleteSlot)

const PROFESSIONAL = { slug: 'ana-ribeiro-bh-01', fullName: 'Ana Ribeiro', specialty: 'Encanador' }
const TIMEZONE = 'America/Sao_Paulo'

const AVAILABLE_SLOT: AgendaSlotItem = { id: 1, start: '2026-10-03T12:00:00Z', end: '2026-10-03T13:00:00Z', status: 'available' }
const BOOKED_SLOT: AgendaSlotItem = { id: 2, start: '2026-10-04T14:00:00Z', end: '2026-10-04T15:00:00Z', status: 'booked' }

function slotsSuccess(slots: readonly AgendaSlotItem[]): ListSlotsResult {
  return { ok: true, data: { professional: PROFESSIONAL, timezone: TIMEZONE, slots } }
}

// Início/fim digitados nos campos `datetime-local` (hora LOCAL do navegador, sem fuso) — o MESMO
// texto que a implementação (`localDateTimeToIsoUtc`, ProfessionalAgenda.tsx) converte via
// `new Date(...).toISOString()`. O teste computa o ISO esperado com a MESMA conversão (não um
// literal cravado): assim a asserção não depende do fuso horário da máquina que roda o teste.
const START_LOCAL = '2026-10-05T14:00'
const END_LOCAL = '2026-10-05T15:00'
const START_ISO = new Date(START_LOCAL).toISOString()
const END_ISO = new Date(END_LOCAL).toISOString()

const NEW_SLOT: AgendaSlotItem = { id: 3, start: START_ISO, end: END_ISO, status: 'available' }

function publishSuccess(): PublishSlotResult {
  return { ok: true, data: NEW_SLOT }
}

function deleteSuccess(): DeleteSlotResult {
  return { ok: true }
}

describe('ProfessionalAgenda', () => {
  beforeEach(() => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([AVAILABLE_SLOT]))
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('o aviso de demonstração sem autenticação é PERMANENTE — aparece antes mesmo da agenda carregar (spec.md D2)', () => {
    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    // Sem esperar o carregamento: o aviso precisa existir já no primeiro render, não só depois do
    // GET /slots resolver — é a mesma lição de "região viva persistente" aplicada a um aviso fixo.
    expect(screen.getByText('Demonstração sem autenticação.')).toBeDefined()
    expect(screen.getByText(/qualquer pessoa que tenha este endereço pode publicar ou remover/)).toBeDefined()
    expect(screen.getByText('Carregando agenda…')).toBeDefined()
  })

  it('carrega a agenda e mostra o formulário com labels de verdade (não placeholder)', async () => {
    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Ana Ribeiro' })).toBeDefined()
    })

    const startInput = screen.getByLabelText('Início')
    const endInput = screen.getByLabelText('Fim')
    expect(startInput.tagName).toBe('INPUT')
    expect(endInput.tagName).toBe('INPUT')
    expect(startInput.getAttribute('type')).toBe('datetime-local')
    expect(endInput.getAttribute('type')).toBe('datetime-local')
  })

  it('a região aria-live do resultado já existe (vazia) ANTES de qualquer publicação/remoção', () => {
    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    const region = screen.getByRole('region', { name: 'Resultado da agenda' })
    expect(region.getAttribute('aria-live')).toBe('polite')
    expect(region.textContent).toBe('')
  })

  it('feliz: publica um horário (submit por Enter — submissão nativa do form) e o horário aparece na lista', async () => {
    publishSlotMock.mockResolvedValue(publishSuccess())

    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByLabelText('Início')).toBeDefined()
    })

    fireEvent.change(screen.getByLabelText('Início'), { target: { value: START_LOCAL } })
    fireEvent.change(screen.getByLabelText('Fim'), { target: { value: END_LOCAL } })

    // Submissão nativa do form (mesmo padrão de SearchForm.test.tsx "submeter com Enter"): um
    // <form> com um único botão de submit dispara este MESMO evento quando o usuário aperta Enter
    // em qualquer campo de texto dele — não há handler de teclado separado para testar.
    fireEvent.submit(screen.getByRole('form', { name: 'Publicar horário' }))

    expect(publishSlotMock).toHaveBeenCalledWith('ana-ribeiro-bh-01', START_ISO, END_ISO, expect.anything())

    const region = screen.getByRole('region', { name: 'Resultado da agenda' })
    await waitFor(() => {
      expect(within(region).getByText(/Horário publicado:/)).toBeDefined()
    })

    // O novo horário passa a existir na lista — sem precisar de um segundo GET.
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
  })

  it('409 slot_overlap: mostra o detail da API — não texto inventado, não o texto de 503', async () => {
    publishSlotMock.mockResolvedValue({
      ok: false,
      error: { code: 'slot_overlap', detail: 'Este horário sobrepõe outro já publicado para este profissional. Escolha um intervalo diferente.' },
    })

    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByLabelText('Início')).toBeDefined()
    })

    fireEvent.change(screen.getByLabelText('Início'), { target: { value: START_LOCAL } })
    fireEvent.change(screen.getByLabelText('Fim'), { target: { value: END_LOCAL } })
    fireEvent.submit(screen.getByRole('form', { name: 'Publicar horário' }))

    await waitFor(() => {
      expect(
        screen.getByText('Este horário sobrepõe outro já publicado para este profissional. Escolha um intervalo diferente.'),
      ).toBeDefined()
    })
    expect(screen.queryByText(/tente novamente em instantes/i)).toBeNull()
  })

  it('delete bloqueado (409 slot_has_reservation): mostra o detail da API e o horário reservado continua na lista', async () => {
    fetchProfessionalSlotsMock.mockResolvedValue(slotsSuccess([BOOKED_SLOT]))
    deleteSlotMock.mockResolvedValue({
      ok: false,
      error: { code: 'slot_has_reservation', detail: 'Este horário já tem uma reserva e não pode ser removido.' },
    })

    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Remover' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Remover' }))

    expect(deleteSlotMock).toHaveBeenCalledWith('ana-ribeiro-bh-01', BOOKED_SLOT.id, expect.anything())

    await waitFor(() => {
      expect(screen.getByText('Este horário já tem uma reserva e não pode ser removido.')).toBeDefined()
    })
    expect(screen.getAllByRole('listitem')).toHaveLength(1)
  })

  it('feliz: remove um horário e ele some da lista', async () => {
    deleteSlotMock.mockResolvedValue(deleteSuccess())

    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={vi.fn()} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Remover' })).toBeDefined()
    })
    fireEvent.click(screen.getByRole('button', { name: 'Remover' }))

    const region = screen.getByRole('region', { name: 'Resultado da agenda' })
    await waitFor(() => {
      expect(within(region).getByText('Horário removido da agenda.')).toBeDefined()
    })
    expect(screen.queryByRole('listitem')).toBeNull()
  })

  it('"Voltar à busca" aciona onNavigate para a view de busca, sem recarregar a página', () => {
    const onNavigate = vi.fn()

    render(<ProfessionalAgenda slug="ana-ribeiro-bh-01" onNavigate={onNavigate} />)

    fireEvent.click(screen.getByRole('link', { name: '← Voltar à busca' }))

    expect(onNavigate).toHaveBeenCalledWith({ kind: 'search' })
  })
})
