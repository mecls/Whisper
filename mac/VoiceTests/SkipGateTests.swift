import XCTest
@testable import Voice

/// prd-sub-second-dictation.md §5: a fixture table covering every clause of rule 11 individually,
/// in both languages. The gate is asymmetric on purpose — every clause has to pass before it skips,
/// because a false negative costs one round trip and a false positive pastes a filler into
/// somebody's document. These tests exist to keep that asymmetry honest as the thresholds move.
final class SkipGateTests: XCTestCase {

    private func reason(_ raw: String, mode: String = "clean", language: String? = "pt") -> SkipGate.Reason? {
        SkipGate.reasonToClean(raw: raw, mode: mode, language: language)
    }

    // MARK: - Skips: short, well-formed, nothing for a cleanup engine to do

    func testSkipsShortWellFormedUtterances() {
        let skippable: [(String, String?)] = [
            ("Bom dia, a reunião está confirmada.", "pt"),
            ("O relatório já foi enviado.", "pt"),
            ("O que achas disto?", "pt"),
            ("Sim.", "pt"),
            ("Isto é bom.", "pt"),                      // bare "é" is the verb, not a filler
            ("The invoice is ready for review.", "en"),
            ("Thanks, see you tomorrow.", "en"),
            ("Can you send me the file?", "en"),
            ("I like this design.", "en"),              // bare "like" is the verb, not a filler
            ("Confirmed!", "en"),
        ]
        for (text, lang) in skippable {
            XCTAssertNil(reason(text, language: lang), "should have skipped cleanup: \(text)")
        }
    }

    // MARK: - Each clause of rule 11, one at a time

    func testTooLongGoesToCleanup() {
        let long = "isto é uma frase bastante longa com muitas palavras seguidas para passar o limite do gate."
        XCTAssertEqual(reason(long), .tooLong)
    }

    func testUnambiguousFillersGoToCleanup() {
        XCTAssertEqual(reason("Um, the meeting is confirmed.", language: "en"), .filler)
        XCTAssertEqual(reason("Uh, I will send it.", language: "en"), .filler)
        XCTAssertEqual(reason("Hã, o pedido foi confirmado.", language: "pt"), .filler)
    }

    func testContextualFillersOnlyCountWhenElongatedOrCommaAdjacent() {
        // Elongated: can only be hesitation.
        XCTAssertEqual(reason("Ééé preciso enviar isto.", language: "pt"), .filler)
        // Comma-adjacent: the way "tipo" and "like" actually appear as fillers in speech.
        XCTAssertEqual(reason("Tipo, o relatório está pronto.", language: "pt"), .filler)
        XCTAssertEqual(reason("It was, like, really good.", language: "en"), .filler)
        // The same words used normally must not trigger it, or no Portuguese sentence ever skips.
        XCTAssertNil(reason("Que tipo de ficheiro é?", language: "pt"))
        XCTAssertNil(reason("I like this design.", language: "en"))
    }

    func testMultiWordFillersGoToCleanup() {
        XCTAssertEqual(reason("You know, the demo went well.", language: "en"), .filler)
        XCTAssertEqual(reason("I mean, we should ship it.", language: "en"), .filler)
    }

    func testRepeatedWordGoesToCleanup() {
        XCTAssertEqual(reason("O pedido já já foi confirmado.", language: "pt"), .repeatedWord)
        XCTAssertEqual(reason("We need to to finalize this.", language: "en"), .repeatedWord)
    }

    func testSpokenCommandsGoToCleanup() {
        // The gate is forbidden from editing text (rule 13), so turning these into real line breaks
        // is the one job only a cleanup engine can do.
        XCTAssertEqual(reason("Obrigado pela reunião nova linha vamos avançar.", language: "pt"), .spokenCommand)
        XCTAssertEqual(reason("Thanks for the call new paragraph let me know.", language: "en"), .spokenCommand)
    }

    func testUnterminatedGoesToCleanup() {
        // Whisper normally punctuates; text that came back without a terminal mark is the signal
        // that something about this transcript is not well formed.
        XCTAssertEqual(reason("o relatório está pronto", language: "pt"), .unterminated)
        XCTAssertEqual(reason("the invoice is ready", language: "en"), .unterminated)
    }

    func testLiteralModeNeverReachesTheGate() {
        XCTAssertEqual(reason("Sim.", mode: "literal"), .notCleanMode)
    }

    func testUnknownLanguageAppliesBothFillerLists() {
        // Rule 12: an unrecognised language must not become a reason to skip.
        XCTAssertEqual(reason("Hã, está confirmado.", language: nil), .filler)
        XCTAssertEqual(reason("Um, it is confirmed.", language: nil), .filler)
    }

    // MARK: - The real corpus

    func testEveryBenchFixtureGoesToCleanup() {
        // The 20 fixtures in server/src/cli/bench-fixtures.ts are all written with fillers, repeats
        // or spoken commands — every one of them is exactly what cleanup exists for, so a gate that
        // skipped any of them would be pasting known-dirty text.
        let fixtures = [
            "hã então, o cliente ligou hoje de manhã, tipo, para confirmar a reunião de amanhã",
            "ééé preciso de enviar aquele, aquele orçamento antes de sexta, tipo, sem falta",
            "boa tarde, hã, isto é só para dizer que o pedido já foi, já foi confirmado",
            "olha, um, temos que remarcar a chamada com o fornecedor, hã, para a próxima semana",
            "tipo, acho que o relatório está pronto mas falta, falta rever os números finais",
            "hã hã então olá, era só para avisar que vou chegar atrasado hoje",
            "um so the the client wants to move the meeting to, uh, Thursday afternoon",
            "okay uh just a quick note, we need to, we need to finalize the invoice today",
            "so um the shipment got delayed and, and we should let the customer know",
            "uh yeah so basically the the demo went really well, everyone was happy",
            "just wanted to say the the new pricing sheet is ready for review",
            "um can you uh remind me to follow up with the supplier tomorrow morning",
            "hã, o meeting de amanhã foi cancelado, temos que reagendar, tipo, para next week",
            "preciso que envies o invoice para o cliente, hã, ainda hoje se for possível",
            "okay so o fornecedor confirmou a entrega, tipo, but it might slip to Friday",
            "boa tarde, obrigado pela reunião de hoje nova linha vamos avançar com a proposta",
            "thanks for the call today new paragraph let me know if you need anything else",
            "o que achas disto",
            "sim",
            "vinte e cinco euros às três e meia",
        ]
        for f in fixtures {
            XCTAssertNotNil(reason(f, language: nil), "must not skip a known-dirty fixture: \(f)")
        }
    }
}
