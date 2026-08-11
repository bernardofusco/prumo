import { useId } from 'react'

/**
 * Consultas de demonstração clicáveis (design.md §7; spec.md D8: "as consultas de demonstração
 * da tela são exatamente as do golden set"). Preenche o campo de busca ao clicar — quem decide o
 * que acontece com o texto (inclusive disparar a busca ou não) é quem passa `onSelect`.
 *
 * Lista vazia é estado ESPERADO, não excepcional: sem o artefato de vetores das consultas
 * (`eval/embeddings/<modelo>.json`, T10, gate humano), `GET /api/search/options` devolve
 * `exampleQueries: []` (spec.md §"GET /api/search/options"). Renderiza `null` em vez de uma
 * lista/rótulo vazios — nada de "Consultas de demonstração" pairando sobre uma `<ul>` sem itens.
 *
 * `useId()` (não um `id` fixo): este componente é montado em DOIS lugares ao mesmo tempo assim
 * que a T10 existir — dentro do `SearchForm` (sempre) e dentro do bloco 422 (quando a consulta
 * não tem vetor pré-computado, App.tsx). Um `id="example-queries-label"` fixo duplicava no DOM
 * (achado do Reviewer, reproduzido com as duas listas montadas juntas): HTML inválido,
 * `aria-labelledby` resolvendo sempre para o primeiro rótulo, dois "Consultas de demonstração"
 * indistinguíveis para leitor de tela. `useId()` gera um sufixo único por instância do componente,
 * estável entre re-renders — a mesma garantia que o React dá para SSR.
 */
export interface ExampleQueriesProps {
  readonly queries: readonly string[]
  readonly onSelect: (query: string) => void
}

export function ExampleQueries({ queries, onSelect }: ExampleQueriesProps) {
  const labelId = useId()

  if (queries.length === 0) {
    return null
  }

  return (
    <div className="example-queries">
      <span id={labelId}>Consultas de demonstração</span>
      <ul aria-labelledby={labelId}>
        {queries.map((query) => (
          <li key={query}>
            <button type="button" onClick={() => onSelect(query)}>
              {query}
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}
