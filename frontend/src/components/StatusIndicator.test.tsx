import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { StatusIndicator } from './StatusIndicator'

describe('StatusIndicator', () => {
  it('renderiza "verificando…" com role="status" quando state é checking', () => {
    render(<StatusIndicator state="checking" />)

    const indicator = screen.getByRole('status')

    expect(indicator.textContent).toBe('verificando…')
  })

  it('renderiza "API online" com role="status" quando state é online', () => {
    render(<StatusIndicator state="online" />)

    const indicator = screen.getByRole('status')

    expect(indicator.textContent).toBe('API online')
  })

  it('renderiza "API indisponível" com role="status" quando state é offline', () => {
    render(<StatusIndicator state="offline" />)

    const indicator = screen.getByRole('status')

    expect(indicator.textContent).toBe('API indisponível')
  })
})
