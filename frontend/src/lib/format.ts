/**
 * Formatação de números para exibição — e só isso. Nenhuma conta mora aqui: os valores que
 * `formatDistanceKm`/`formatScore` recebem já saem prontos de `GET /api/search`
 * (`factors.*`, `distanceKm`, `score` — design.md §6, spec.md "Contrato API ↔ Frontend": "a API
 * explica, o React renderiza"). Centralizar a formatação aqui evita que cada componente escolha a
 * própria precisão/locale (design.md §7: "formatação mora aqui, não espalhada nos componentes").
 */

// `Intl.NumberFormat` é reaproveitável entre chamadas (a documentação do próprio construtor
// recomenda cache de instância em vez de recriar por formatação) — por isso vive em módulo, não
// dentro das funções abaixo.
const KM_FORMATTER = new Intl.NumberFormat('pt-BR', {
  minimumFractionDigits: 1,
  maximumFractionDigits: 1,
})

const SCORE_FORMATTER = new Intl.NumberFormat('pt-BR', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/**
 * Formata uma distância em quilômetros com 1 casa decimal, locale pt-BR (vírgula decimal).
 *
 * Recebe `number`, não `number | null`, de propósito: `distanceKm` só é `null` quando a busca não
 * tem localização (D4 da spec), e nesse caso a tela não mostra distância nenhuma — a decisão de
 * chamar (ou não) esta função é do componente que sabe se há localização, não desta função pura.
 */
export function formatDistanceKm(distanceKm: number): string {
  return KM_FORMATTER.format(distanceKm)
}

/**
 * Formata um score (ou um fator do score) com 2 casas decimais, locale pt-BR (vírgula decimal).
 *
 * Mesma disciplina de `formatDistanceKm`: recebe `number`, não `number | null` — `factors.proximity`
 * e `factors.proximityContribution` só são `null` sem localização, e é o componente quem decide se
 * chama esta função.
 */
export function formatScore(score: number): string {
  return SCORE_FORMATTER.format(score)
}
