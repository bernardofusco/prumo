/// <reference types="vite/client" />

interface ImportMetaEnv {
  /**
   * Base da API para build de produção (specs/features/met-477-fundacao-repos-e-gates/spec.md,
   * "Contrato API ↔ Frontend"). Nome da variável documentado em .env.example; valor nunca
   * versionado. Ausente em dev — o proxy do Vite (vite.config.ts) cobre o caminho relativo.
   */
  readonly VITE_API_BASE_URL?: string
}
