import { useEffect, useState } from 'react'

import './App.css'
import { fetchHealth } from './api/health'
import { StatusIndicator, type StatusIndicatorState } from './components/StatusIndicator'

function App() {
  const [state, setState] = useState<StatusIndicatorState>('checking')

  useEffect(() => {
    let cancelled = false

    const checkHealth = async () => {
      const result = await fetchHealth()
      if (!cancelled) {
        setState(result)
      }
    }

    void checkHealth()

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <main>
      <h1>Prumo</h1>
      <p>
        Estado da API: <StatusIndicator state={state} />
      </p>
    </main>
  )
}

export default App
