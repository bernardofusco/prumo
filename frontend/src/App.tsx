import './App.css'
import { StatusIndicator } from './components/StatusIndicator'

function App() {
  // Estado fixo por enquanto: o fetch real do estado de saúde da API é a task T7.
  return (
    <main>
      <h1>Prumo</h1>
      <p>
        Estado da API: <StatusIndicator state="checking" />
      </p>
    </main>
  )
}

export default App
