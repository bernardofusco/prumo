import type { ReactNode } from 'react'

/**
 * Aviso reutilizável (design.md §7: "aviso reutilizável (sem localização | degradado |
 * indisponível)"). Sem regra de negócio: quem decide QUANDO mostrar e o QUE dizer é quem
 * renderiza `<Notice>`; este componente só decide o papel ARIA e a classe visual pelo `tone`.
 *
 * `tone="error"` usa `role="alert"` (interrompe o leitor de tela — falha real, ex.: 502/rede).
 * `info`/`warning` usam `role="status"` (`aria-live="polite"` implícito pelo papel ARIA) —
 * inclusive o aviso PERMANENTE de "sem localização" (D4 da spec): permanente não é urgente, é
 * um fato sobre a busca que fica visível o tempo todo, sem botão de fechar.
 *
 * `announce`: **false** quando este `Notice` já nasce dentro de outra região `aria-live`
 * (achado do Reviewer — nesting de `role="status"`/`role="alert"` dentro de um `aria-live="polite"`
 * externo tende a duplicar o anúncio no leitor de tela). Quem envolve o `Notice` numa região viva
 * própria (ex.: `App.tsx`, a região de resultados) passa `announce={false}`; quem usa `Notice`
 * isolado (ex.: falha ao carregar `/options`, fora de qualquer região viva) usa o padrão `true`.
 */
export type NoticeTone = 'info' | 'warning' | 'error'

export interface NoticeProps {
  readonly tone: NoticeTone
  readonly children: ReactNode
  readonly announce?: boolean
}

export function Notice({ tone, children, announce = true }: NoticeProps) {
  const role = announce ? (tone === 'error' ? 'alert' : 'status') : undefined

  return (
    <div className={`notice notice--${tone}`} role={role}>
      {children}
    </div>
  )
}
