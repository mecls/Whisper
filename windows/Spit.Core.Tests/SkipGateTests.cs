namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/SkipGateTests.swift. prd-sub-second-dictation.md §5: a fixture table covering
/// every clause of rule 11 individually, in both languages. The gate is asymmetric on purpose — a false
/// negative costs one round trip and a false positive pastes a filler into somebody's document. These
/// tests keep that asymmetry honest as the thresholds move.
public sealed class SkipGateTests
{
    private static SkipGate.Reason? reason(string raw, string mode = "clean", string? language = "pt") =>
        SkipGate.ReasonToClean(raw, mode, language);

    // MARK: - Skips: short, well-formed, nothing for a cleanup engine to do

    [Fact]
    public void testSkipsShortWellFormedUtterances()
    {
        (string Text, string? Lang)[] skippable =
        [
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
        ];
        foreach (var (text, lang) in skippable)
            Assert.True(reason(text, language: lang) is null, $"should have skipped cleanup: {text}");
    }

    // MARK: - Each clause of rule 11, one at a time

    [Fact]
    public void testTooLongGoesToCleanup()
    {
        var @long = "isto é uma frase bastante longa com muitas palavras seguidas para passar o limite do gate.";
        Assert.Equal(SkipGate.Reason.TooLong, reason(@long));
    }

    [Fact]
    public void testUnambiguousFillersGoToCleanup()
    {
        Assert.Equal(SkipGate.Reason.Filler, reason("Um, the meeting is confirmed.", language: "en"));
        Assert.Equal(SkipGate.Reason.Filler, reason("Uh, I will send it.", language: "en"));
        Assert.Equal(SkipGate.Reason.Filler, reason("Hã, o pedido foi confirmado.", language: "pt"));
    }

    [Fact]
    public void testContextualFillersOnlyCountWhenElongatedOrCommaAdjacent()
    {
        // Elongated: can only be hesitation.
        Assert.Equal(SkipGate.Reason.Filler, reason("Ééé preciso enviar isto.", language: "pt"));
        // Comma-adjacent: the way "tipo" and "like" actually appear as fillers in speech.
        Assert.Equal(SkipGate.Reason.Filler, reason("Tipo, o relatório está pronto.", language: "pt"));
        Assert.Equal(SkipGate.Reason.Filler, reason("It was, like, really good.", language: "en"));
        // The same words used normally must not trigger it, or no Portuguese sentence ever skips.
        Assert.Null(reason("Que tipo de ficheiro é?", language: "pt"));
        Assert.Null(reason("I like this design.", language: "en"));
    }

    [Fact]
    public void testMultiWordFillersGoToCleanup()
    {
        Assert.Equal(SkipGate.Reason.Filler, reason("You know, the demo went well.", language: "en"));
        Assert.Equal(SkipGate.Reason.Filler, reason("I mean, we should ship it.", language: "en"));
    }

    [Fact]
    public void testRepeatedWordGoesToCleanup()
    {
        Assert.Equal(SkipGate.Reason.RepeatedWord, reason("O pedido já já foi confirmado.", language: "pt"));
        Assert.Equal(SkipGate.Reason.RepeatedWord, reason("We need to to finalize this.", language: "en"));
    }

    [Fact]
    public void testSpokenCommandsGoToCleanup()
    {
        // The gate is forbidden from editing text (rule 13), so turning these into real line breaks is the
        // one job only a cleanup engine can do.
        Assert.Equal(SkipGate.Reason.SpokenCommand, reason("Obrigado pela reunião nova linha vamos avançar.", language: "pt"));
        Assert.Equal(SkipGate.Reason.SpokenCommand, reason("Thanks for the call new paragraph let me know.", language: "en"));
    }

    [Fact]
    public void testUnterminatedGoesToCleanup()
    {
        // Whisper normally punctuates; text without a terminal mark signals a transcript that is not well
        // formed.
        Assert.Equal(SkipGate.Reason.Unterminated, reason("o relatório está pronto", language: "pt"));
        Assert.Equal(SkipGate.Reason.Unterminated, reason("the invoice is ready", language: "en"));
    }

    [Fact]
    public void testLiteralModeNeverReachesTheGate()
    {
        Assert.Equal(SkipGate.Reason.NotCleanMode, reason("Sim.", mode: "literal"));
    }

    [Fact]
    public void testUnknownLanguageAppliesBothFillerLists()
    {
        // Rule 12: an unrecognised language must not become a reason to skip.
        Assert.Equal(SkipGate.Reason.Filler, reason("Hã, está confirmado.", language: null));
        Assert.Equal(SkipGate.Reason.Filler, reason("Um, it is confirmed.", language: null));
    }

    // MARK: - The real corpus

    [Fact]
    public void testEveryBenchFixtureGoesToCleanup()
    {
        // The 20 fixtures in server/src/cli/bench-fixtures.ts are all written with fillers, repeats or
        // spoken commands — a gate that skipped any of them would be pasting known-dirty text.
        string[] fixtures =
        [
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
        ];
        foreach (var f in fixtures)
            Assert.True(reason(f, language: null) is not null, $"must not skip a known-dirty fixture: {f}");
    }

    // MARK: - Streamed transcripts (prd-live-transcription.md rule 4a)

    [Fact]
    public void testASingleSegmentStreamedTranscriptSkipsExactlyAsBefore()
    {
        // One confirmed segment means no chunk boundary, so the fast path stays available — which matters
        // most for exactly these short utterances, where the round trip is the largest share of latency.
        Assert.Null(SkipGate.ReasonToClean("Bom dia, a reunião está confirmada.", mode: "clean", language: "pt", segments: 1));
    }

    [Fact]
    public void testAMultiSegmentStreamedTranscriptNeverSkips()
    {
        // The same text, assembled from two segments, must go to cleanup however clean it looks.
        Assert.Equal(SkipGate.Reason.StreamedMultiSegment,
            SkipGate.ReasonToClean("Bom dia, a reunião está confirmada.", mode: "clean", language: "pt", segments: 2));
    }

    [Fact]
    public void testTheSegmentRuleOutranksEveryOtherClause()
    {
        // Even text that would pass every clause of rule 11 is still cleaned when it carries a boundary —
        // the other clauses cannot see a seam mid-sentence.
        foreach (var text in new[] { "Sim.", "The invoice is ready for review.", "O que achas disto?" })
            Assert.True(SkipGate.ReasonToClean(text, mode: "clean", language: null, segments: 3) == SkipGate.Reason.StreamedMultiSegment,
                $"{text} carries a boundary and must be cleaned");
    }

    [Fact]
    public void testLiteralModeStillWinsOverTheSegmentRule()
    {
        // Literal is the user's explicit instruction not to touch the text; a chunk boundary does not
        // override that.
        Assert.Equal(SkipGate.Reason.NotCleanMode, SkipGate.ReasonToClean("Sim.", mode: "literal", language: "pt", segments: 4));
    }

    [Fact]
    public void testOnePassTranscriptionIsUnaffectedByDefault()
    {
        // Every existing caller omits `segments`, so the default of 1 must preserve today's behaviour —
        // otherwise this rule would silently disable the skip gate.
        Assert.Null(SkipGate.ReasonToClean("Sim.", mode: "clean", language: "pt"));
        Assert.True(SkipGate.ShouldSkipCleanup("Isto é bom.", mode: "clean", language: "pt"));
    }
}
