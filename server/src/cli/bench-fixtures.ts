/**
 * 20 realistic dictation transcripts for `bench.ts`, written as a PT/EN
 * bilingual speaker would actually say them into a phone: fillers, repeats,
 * false starts. No real client names. Mix per Task 8 brief:
 *   6 pt-PT · 6 en · 3 mixed pt/en · 2 spoken commands · 1 question ·
 *   1 one-word · 1 spelled-out numbers.
 */
export const BENCH_FIXTURES: string[] = [
  // 6 pt-PT, fillers/repeats
  'hã então, o cliente ligou hoje de manhã, tipo, para confirmar a reunião de amanhã',
  'ééé preciso de enviar aquele, aquele orçamento antes de sexta, tipo, sem falta',
  'boa tarde, hã, isto é só para dizer que o pedido já foi, já foi confirmado',
  'olha, um, temos que remarcar a chamada com o fornecedor, hã, para a próxima semana',
  'tipo, acho que o relatório está pronto mas falta, falta rever os números finais',
  'hã hã então olá, era só para avisar que vou chegar atrasado hoje',
  // 6 en, fillers/repeats
  'um so the the client wants to move the meeting to, uh, Thursday afternoon',
  'okay uh just a quick note, we need to, we need to finalize the invoice today',
  'so um the shipment got delayed and, and we should let the customer know',
  'uh yeah so basically the the demo went really well, everyone was happy',
  'just wanted to say the the new pricing sheet is ready for review',
  'um can you uh remind me to follow up with the supplier tomorrow morning',
  // 3 mixed pt/en
  'hã, o meeting de amanhã foi cancelado, temos que reagendar, tipo, para next week',
  'preciso que envies o invoice para o cliente, hã, ainda hoje se for possível',
  'okay so o fornecedor confirmou a entrega, tipo, but it might slip to Friday',
  // 2 spoken commands
  'boa tarde, obrigado pela reunião de hoje nova linha vamos avançar com a proposta',
  'thanks for the call today new paragraph let me know if you need anything else',
  // 1 question
  'o que achas disto',
  // 1 one-word
  'sim',
  // 1 spelled-out numbers
  'vinte e cinco euros às três e meia',
]
