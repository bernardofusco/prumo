const COORDINATE_DECIMAL_PLACES = 3
const ROUNDING_FACTOR = 10 ** COORDINATE_DECIMAL_PLACES

/**
 * Arredonda uma coordenada (latitude ou longitude) para 3 casas decimais (~110 m no equador)
 * antes de ela virar parâmetro de `GET /api/search`.
 *
 * Por quê (spec.md, "Segredos e Custo Externo" — "Dado pessoal — o ponto novo desta issue"): a
 * coordenada é o primeiro dado pessoal que o produto recebe do usuário. O ranking não precisa da
 * precisão total do GPS do navegador para funcionar — `Proximity(km, τ)` já é uma curva suave, e
 * ~110 m de incerteza não muda qual profissional vence. Enviar menos precisão do que se recebeu é
 * minimização de dado por padrão, não um detalhe estético: é a defesa mais barata contra
 * reidentificar onde o usuário está, e ela vive aqui — antes do dado sair do browser — porque
 * qualquer outro lugar (servidor, log) já seria tarde demais.
 */
export function roundCoordinate(value: number): number {
  return Math.round(value * ROUNDING_FACTOR) / ROUNDING_FACTOR
}
