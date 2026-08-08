/**
 * Apresentação pura do estado de saúde da API.
 *
 * Sem fetch, sem regra de negócio: recebe o estado já resolvido e traduz para um
 * rótulo em pt-BR, anunciado via `role="status"` para leitor de tela. A leitura real do
 * estado (fetchHealth) é responsabilidade de outra camada (task T7).
 */
export type StatusIndicatorState = 'checking' | 'online' | 'offline'

const LABEL_BY_STATE: Record<StatusIndicatorState, string> = {
  checking: 'verificando…',
  online: 'API online',
  offline: 'API indisponível',
}

export interface StatusIndicatorProps {
  state: StatusIndicatorState
}

export function StatusIndicator({ state }: StatusIndicatorProps) {
  return <span role="status">{LABEL_BY_STATE[state]}</span>
}
