import type { StatusIndicatorState } from '../components/StatusIndicator'

/**
 * Contrato de GET /api/health (specs/features/met-477-fundacao-repos-e-gates/spec.md, "Contrato
 * API ↔ Frontend"). Só o campo que o frontend efetivamente consome é tipado aqui.
 */
interface HealthResponse {
  status: string
}

// Em dev, caminho relativo — o proxy do Vite (vite.config.ts) encaminha /api para a porta fixa
// da API, sem CORS. Em build, `VITE_API_BASE_URL` (nome documentado em .env.example; valor nunca
// versionado) aponta para a API publicada. Nada de URL hardcoded em componente.
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

/**
 * Consulta a saúde da API e traduz a resposta no estado que `StatusIndicator` sabe renderizar.
 *
 * Sem regra de negócio: nenhuma dedução própria de disponibilidade, nenhum retry. Resposta HTTP
 * não-ok, corpo inesperado ou falha de rede — tudo vira `offline`; nenhuma exceção escapa daqui.
 */
export async function fetchHealth(): Promise<StatusIndicatorState> {
  try {
    const response = await fetch(`${API_BASE_URL}/api/health`)

    if (!response.ok) {
      return 'offline'
    }

    const body = (await response.json()) as HealthResponse
    return body.status === 'ok' ? 'online' : 'offline'
  } catch {
    return 'offline'
  }
}
