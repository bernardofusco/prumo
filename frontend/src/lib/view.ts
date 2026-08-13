/**
 * Roteamento do frontend SEM biblioteca de rotas (spec.md D6 da MET-480: `react-router` está
 * proibido; History API cobre as três telas — design.md §10). `parseView`/`toPath` são as duas
 * pontas puras (sem `window`, sem efeito colateral) que uma futura `useView` (T13/T14, junto de
 * `App.tsx`) vai usar para ler `location.pathname` e escrever `history.pushState`; nenhuma delas
 * mora aqui de propósito — T12 é "sem tela ainda" (tasks.md).
 */

export type View =
  | { readonly kind: 'search' }
  | { readonly kind: 'professional'; readonly slug: string }
  | { readonly kind: 'professionalAgenda'; readonly slug: string }

const PROFESSIONAL_PATH_PREFIX = '/profissional/'
const AGENDA_PATH_SUFFIX = '/agenda'

/**
 * `pathname` → {@link View}. Qualquer caminho que não seja um dos três reconhecidos (spec.md D6:
 * `/`, `/profissional/:slug`, `/profissional/:slug/agenda`) — incluindo um `:slug` vazio — cai de
 * volta em `search`: não há tela de "404" nesta feature, e a busca (MET-479) é a tela inicial
 * segura para qualquer endereço desconhecido.
 */
export function parseView(pathname: string): View {
  const path = normalizePath(pathname)

  if (path === '' || path === '/') {
    return { kind: 'search' }
  }

  if (!path.startsWith(PROFESSIONAL_PATH_PREFIX)) {
    return { kind: 'search' }
  }

  const rest = path.slice(PROFESSIONAL_PATH_PREFIX.length)

  if (rest.endsWith(AGENDA_PATH_SUFFIX)) {
    const slug = rest.slice(0, -AGENDA_PATH_SUFFIX.length)

    return isValidSlugSegment(slug)
      ? { kind: 'professionalAgenda', slug: decodeURIComponent(slug) }
      : { kind: 'search' }
  }

  return isValidSlugSegment(rest) ? { kind: 'professional', slug: decodeURIComponent(rest) } : { kind: 'search' }
}

/** {@link View} → `pathname` (o inverso de {@link parseView} — usado para montar `href`/`pushState`). */
export function toPath(view: View): string {
  switch (view.kind) {
    case 'search':
      return '/'
    case 'professional':
      return `${PROFESSIONAL_PATH_PREFIX}${encodeURIComponent(view.slug)}`
    case 'professionalAgenda':
      return `${PROFESSIONAL_PATH_PREFIX}${encodeURIComponent(view.slug)}${AGENDA_PATH_SUFFIX}`
  }
}

/** Um segmento de slug válido: não vazio e sem `/` interno (senão não é UM segmento, são dois). */
function isValidSlugSegment(segment: string): boolean {
  return segment.length > 0 && !segment.includes('/')
}

/**
 * Descarta query string e fragmento (um `pathname` de verdade — `location.pathname` — nunca os
 * carrega, mas esta função aceita a entrada crua de um teste ou de uma URL completa colada por
 * engano) e a barra final (exceto a raiz), para que `/profissional/ana/` e `/profissional/ana`
 * sejam a MESMA {@link View}.
 */
function normalizePath(pathname: string): string {
  const withoutFragment = pathname.split('#')[0] ?? ''
  const withoutQuery = withoutFragment.split('?')[0] ?? ''

  return withoutQuery.length > 1 && withoutQuery.endsWith('/') ? withoutQuery.slice(0, -1) : withoutQuery
}
