/**
 * Identidade mínima do cliente anônimo (spec.md D2 da MET-480, `specs/features/met-480-agendamento-
 * concorrencia/spec.md`): um UUID v4 gerado no browser, persistido em `localStorage` sob a chave
 * `prumo.clientKey` e reenviado em todo `POST /api/reservations` (cabeçalho `X-Prumo-Client-Key`,
 * `api/agenda.ts`). NENHUM dado pessoal — nome, e-mail, telefone, cookie de sessão — nasce aqui nem
 * em lugar nenhum desta função: é só um identificador opaco para distinguir "o mesmo cliente tentou
 * de novo" (replay, spec.md F3) de "outro cliente ganhou a corrida" (conflito, spec.md F2).
 *
 * `getClientKey()` é get-or-create: a primeira chamada num browser cria a chave; toda chamada
 * seguinte — nesta aba, na próxima, depois de um reload — relê o MESMO valor. Uma janela anônima
 * (ou `localStorage.clear()`) é a única forma de "virar" outro cliente (spec.md D2, jornada J2). O
 * frontend nunca decide identidade de outro jeito — não há login, não há sessão de servidor.
 */

const CLIENT_KEY_STORAGE_KEY = 'prumo.clientKey'

/** UUID v4 exato — dígito de versão `4` e dígito de variante em `{8,9,a,b}` (RFC 4122 §4.4). */
const UUID_V4_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i

/**
 * Get-or-create do `clientKey`. Um valor salvo que não é um UUID v4 válido (storage corrompido,
 * editado à mão) é tratado como ausente — nunca reenviado ao servidor: o cabeçalho
 * `X-Prumo-Client-Key` é validado como UUID pela API (spec.md "Contrato API ↔ Frontend": ausente/
 * inválido ⇒ `400 invalid_request`), e um valor não confiável só produziria esse erro sem motivo.
 */
export function getClientKey(): string {
  const stored = localStorage.getItem(CLIENT_KEY_STORAGE_KEY)

  if (stored !== null && UUID_V4_PATTERN.test(stored)) {
    return stored
  }

  const created = crypto.randomUUID()
  localStorage.setItem(CLIENT_KEY_STORAGE_KEY, created)

  return created
}
