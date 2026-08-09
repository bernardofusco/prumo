import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Wrapper fino sobre `navigator.geolocation.getCurrentPosition` (spec.md F1/D4, design.md §7:
 * "hooks/useGeolocation.ts wrapper fino"). Não decide nada sozinho — não escolhe cidade de
 * fallback, não mostra mensagem: só traduz a API de callback do navegador num estado que
 * `LocationPicker` (T9) consome e interpreta.
 *
 * `unsupported` cobre dois casos com o mesmo tratamento para quem usa o hook: navegador sem
 * `navigator.geolocation` (ex.: contexto inseguro fora de `localhost`, spec "Riscos" R7) e
 * ambiente de teste (jsdom) sem a API implementada.
 *
 * `timeout` existe à parte de `denied` (MET-515): sem um prazo, `getCurrentPosition` pode nunca
 * chamar nem `success` nem `error` — perfil gerenciado que nega em silêncio, máquina sem GPS,
 * política do navegador — e o estado "requesting" fica preso para sempre, sem forma de tentar de
 * novo. Isso não é hipotético: foi exatamente o que o QA de milestone reproduziu.
 */
export type UseGeolocationState =
  | { readonly status: 'idle' }
  | { readonly status: 'requesting' }
  | { readonly status: 'granted'; readonly latitude: number; readonly longitude: number }
  | { readonly status: 'denied' }
  | { readonly status: 'timeout' }
  | { readonly status: 'unsupported' }

export interface UseGeolocationResult {
  readonly state: UseGeolocationState
  readonly request: () => void
}

// GeolocationPositionError.TIMEOUT (W3C Geolocation API, valor fixo pela spec: PERMISSION_DENIED=1,
// POSITION_UNAVAILABLE=2, TIMEOUT=3). Literal em vez de referenciar a constante global porque ela
// só existe num navegador real — jsdom (ambiente de teste) não a implementa.
const GEOLOCATION_ERROR_CODE_TIMEOUT = 3

// Prazo explícito para `getCurrentPosition` (MET-515: sem ele, uma promessa que nunca resolve
// prende "requesting" para sempre). 10s cobre a faixa típica de resolução por Wi-Fi/IP dos
// navegadores de desktop (o cenário desta busca) sem deixar quem tem sinal ruim esperando bem
// mais que isso — GPS de alta precisão (que pode levar dezenas de segundos) não é o caminho comum
// aqui, e a saída de fallback (escolher cidade) é rápida o bastante para não valer a pena esperar
// mais. Também é o prazo do relógio próprio abaixo, que garante a saída mesmo se o navegador nunca
// chamar `success` nem `error` — a causa raiz do defeito, que `PositionOptions.timeout` sozinho
// não cobre porque depende do navegador respeitá-lo.
// Exportado para o teste que reproduz o defeito (MET-515) avançar o relógio pelo mesmo prazo, em
// vez de duplicar o número — a constante aqui é a única fonte da verdade.
export const GEOLOCATION_TIMEOUT_MS = 10_000

export function useGeolocation(): UseGeolocationResult {
  const [state, setState] = useState<UseGeolocationState>({ status: 'idle' })
  // Identifica a tentativa em curso: sem cancelamento real de `getCurrentPosition`, um callback
  // atrasado de uma tentativa anterior (ex.: o usuário tentou de novo depois de um timeout) não
  // pode sobrescrever o estado da tentativa atual.
  const requestIdRef = useRef(0)
  const timeoutIdRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  // Evita um relógio órfão rodando depois que quem usa o hook desmonta (ex.: navegação para outra
  // tela no meio de uma tentativa "requesting").
  useEffect(() => {
    return () => {
      clearTimeout(timeoutIdRef.current)
    }
  }, [])

  const request = useCallback(() => {
    if (typeof navigator === 'undefined' || !navigator.geolocation) {
      setState({ status: 'unsupported' })
      return
    }

    // Encerra qualquer relógio de uma tentativa anterior ainda pendente: sem isto, duas chamadas a
    // `request()` sem a primeira resolver deixam DOIS `setTimeout` vivos ao mesmo tempo — o
    // segundo sobrescreve a referência do primeiro sem cancelá-lo, e o cleanup do unmount só
    // alcança o mais recente (o mais antigo vaza). Inalcançável pela UI atual (o botão fica
    // `disabled` durante "requesting"), mas o hook é API pública — fechado aqui de qualquer forma.
    clearTimeout(timeoutIdRef.current)

    const requestId = ++requestIdRef.current
    setState({ status: 'requesting' })

    timeoutIdRef.current = setTimeout(() => {
      // Verdadeiro sempre em operação normal — a limpeza acima garante no máximo um relógio vivo
      // por vez, sempre o da tentativa atual. Mantido como defesa em profundidade.
      if (requestIdRef.current === requestId) {
        // Abandona esta tentativa ANTES de marcar o estado "timeout": um callback do navegador que
        // ainda chegue depois disto — o navegador que "segura" o callback e responde tarde é
        // exatamente a premissa do defeito (MET-515) — precisa ser descartado pelas guardas de
        // `success`/`error` abaixo, não pode sobrescrever a cidade que o usuário já escolheu
        // obedecendo a este alerta.
        requestIdRef.current += 1
        setState({ status: 'timeout' })
      }
    }, GEOLOCATION_TIMEOUT_MS)

    navigator.geolocation.getCurrentPosition(
      (position) => {
        if (requestIdRef.current !== requestId) return
        clearTimeout(timeoutIdRef.current)
        setState({
          status: 'granted',
          latitude: position.coords.latitude,
          longitude: position.coords.longitude,
        })
      },
      (error) => {
        if (requestIdRef.current !== requestId) return
        clearTimeout(timeoutIdRef.current)
        if (error.code === GEOLOCATION_ERROR_CODE_TIMEOUT) {
          setState({ status: 'timeout' })
          return
        }
        // O navegador não distingue "permissão negada" de "posição indisponível" de forma que
        // valha a pena expor na tela (spec J2: mensagem única, sem detalhe técnico). O timeout
        // (relógio do navegador ou o nosso, acima) tem tratamento e mensagem próprios.
        setState({ status: 'denied' })
      },
      { timeout: GEOLOCATION_TIMEOUT_MS },
    )
  }, [])

  return { state, request }
}
