import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import type { SearchRankingInfo, SearchResultItem } from '../api/search'
import { ResultCard } from './ResultCard'

const RANKING: SearchRankingInfo = {
  semanticWeight: 0.7,
  proximityWeight: 0.3,
  distanceDecayKm: 10,
  minSemanticScore: 0,
}

// Rede de segurança contra o mutante "componente recalcula em vez de exibir" (achado do Reviewer:
// com o fixture anterior, `semantic × semanticWeight` arredondava para O MESMO texto que
// `semanticContribution` — 0,71 × 0,70 = 0,50 e 0,4973 TAMBÉM formatam "0,50"; a troca de
// `formatScore(factors.semanticContribution)` por `formatScore(factors.semantic *
// ranking.semanticWeight)` sobrevivia ao teste). Aqui os números são escolhidos para que o
// PRODUTO (o que um recálculo geraria) e a CONTRIBUIÇÃO (o que a API manda e a tela deve exibir)
// caiam em faixas de 2 casas decimais bem separadas — nenhuma coincidência de arredondamento:
//   semantic × semanticWeight  = 0,50 × 0,70 = 0,35  ≠  semanticContribution  = 0,90
//   proximity × proximityWeight = 0,50 × 0,30 = 0,15  ≠  proximityContribution = 0,60
// Se o componente algum dia recalcular, o texto renderizado muda de "0,90"/"0,60" para
// "0,35"/"0,15" e as asserções abaixo falham.
const RESULT_WITH_LOCATION: SearchResultItem = {
  slug: 'ana-ribeiro-bh-01',
  fullName: 'Ana Ribeiro',
  specialty: 'Encanador',
  city: 'Belo Horizonte',
  state: 'MG',
  serviceDescription: 'Atendo emergência de goteira embaixo da pia.',
  distanceKm: 3.42,
  score: 0.75,
  factors: {
    semantic: 0.5,
    proximity: 0.5,
    semanticContribution: 0.9,
    proximityContribution: 0.6,
  },
}

const RESULT_WITHOUT_LOCATION: SearchResultItem = {
  slug: 'camila-correia-dcx-009',
  fullName: 'Camila Correia',
  specialty: 'Encanador',
  city: 'Duque de Caxias',
  state: 'RJ',
  serviceDescription: 'Atendo com urgência vazamento na parede do banheiro.',
  distanceKm: null,
  score: 0.5,
  factors: {
    semantic: 0.4,
    proximity: null,
    // Assimetria deliberada da spec: NÃO é `semantic` bruto (0,40) nem `semantic *
    // ranking.semanticWeight` (0,28) — um valor que a API decidiu e que só sobrevive no texto se
    // o componente exibir `semanticContribution` sem tocar nele.
    semanticContribution: 0.85,
    proximityContribution: null,
  },
}

describe('ResultCard', () => {
  it('exibe nome, especialidade e cidade/UF exatamente como veio da API', () => {
    render(<ResultCard result={RESULT_WITH_LOCATION} ranking={RANKING} onViewSlots={vi.fn()} />)

    expect(screen.getByRole('heading', { name: 'Ana Ribeiro' })).toBeDefined()
    expect(screen.getByText('Encanador · Belo Horizonte, MG')).toBeDefined()
  })

  it('com localização: mostra distância, score e a decomposição formatados — sem recalcular nada', () => {
    render(<ResultCard result={RESULT_WITH_LOCATION} ranking={RANKING} onViewSlots={vi.fn()} />)

    // formatDistanceKm/formatScore (pt-BR, vírgula decimal) — os números são os do fixture, ponto.
    expect(screen.getByText('3,4 km')).toBeDefined()
    expect(screen.getByText('0,75')).toBeDefined() // score
    // 0,50 × peso 0,70 = 0,90 — o "= 0,90" só aparece se a tela exibir semanticContribution
    // (o produto 0,50 × 0,70 daria "0,35", nunca "0,90").
    expect(screen.getByText('0,50 × peso 0,70 = 0,90')).toBeDefined()
    // 0,50 × peso 0,30 = 0,60 — pela mesma lógica (produto daria "0,15").
    expect(screen.getByText('0,50 × peso 0,30 = 0,60')).toBeDefined()
  })

  it('sem localização: não mostra distância, nem multiplica a relevância por peso nenhum', () => {
    render(<ResultCard result={RESULT_WITHOUT_LOCATION} ranking={RANKING} onViewSlots={vi.fn()} />)

    expect(screen.queryByText(/km$/)).toBeNull()
    expect(screen.queryByText(/× peso/)).toBeNull()
    // "0,85", não "0,40" (semantic bruto) nem "0,28" (semantic × peso) — só sobra se a tela
    // exibir `semanticContribution` tal como veio, sem tentar reconciliar com nenhuma conta.
    expect(
      screen.getByText('0,85 (sem localização, o score considera só a relevância)'),
    ).toBeDefined()
  })

  // MET-480 T13 (spec.md AGN-13/J1/J6): "Ver horários" precisa ser um LINK de verdade (foco e
  // Enter nativos do teclado — nenhum handler de tecla escrito à mão) com nome acessível que
  // identifica QUAL profissional, não só o texto genérico do botão.
  it('"Ver horários" é um link focável, com nome acessível por profissional e href para /profissional/:slug', () => {
    render(<ResultCard result={RESULT_WITH_LOCATION} ranking={RANKING} onViewSlots={vi.fn()} />)

    const link = screen.getByRole('link', { name: 'Ver horários de Ana Ribeiro' })
    expect(link.getAttribute('href')).toBe('/profissional/ana-ribeiro-bh-01')
    expect(screen.getByText('Ver horários')).toBeDefined()
  })

  it('clicar em "Ver horários" aciona onViewSlots com o slug do resultado, sem navegar a página inteira', () => {
    const onViewSlots = vi.fn()
    render(<ResultCard result={RESULT_WITH_LOCATION} ranking={RANKING} onViewSlots={onViewSlots} />)

    fireEvent.click(screen.getByRole('link', { name: 'Ver horários de Ana Ribeiro' }))

    expect(onViewSlots).toHaveBeenCalledTimes(1)
    expect(onViewSlots).toHaveBeenCalledWith('ana-ribeiro-bh-01')
  })
})
