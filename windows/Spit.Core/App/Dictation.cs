using System.Text.Json.Serialization;

namespace Spit.Core;

// Port of mac/Voice/App/Dictation.swift. Types keep their Mac names so a rule can be traced across
// the two clients by searching for one word.

/// The app the user was dictating into, captured at hotkey-down. On Windows `BundleId` is the
/// foreground executable's file name, lowercased (`chrome.exe`), and `Name` its FileDescription
/// (`Google Chrome`) — never a window title (prd-spit-mac-windows.md rules 26, 31).
public sealed record FrontmostApp(string? BundleId, string? Name);

[JsonConverter(typeof(JsonStringEnumConverter<FallbackReason>))]
public enum FallbackReason
{
    [JsonStringEnumMemberName("client-timeout")] ClientTimeout,
    [JsonStringEnumMemberName("offline")] Offline,
    [JsonStringEnumMemberName("unauthorized")] Unauthorized,
    [JsonStringEnumMemberName("server")] Server,
    [JsonStringEnumMemberName("llm-timeout")] LlmTimeout,
    [JsonStringEnumMemberName("llm-error")] LlmError,
    [JsonStringEnumMemberName("llm-busy")] LlmBusy,
    [JsonStringEnumMemberName("llm-truncated")] LlmTruncated,
    [JsonStringEnumMemberName("guard-rejected")] GuardRejected,
}

[JsonConverter(typeof(JsonStringEnumConverter<Injected>))]
public enum Injected
{
    [JsonStringEnumMemberName("cleaned")] Cleaned,
    [JsonStringEnumMemberName("raw")] Raw,
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("clipboard")] Clipboard,
}

public enum DictationStage { Recording, Transcribing, Refining, ReadyToInsert, Inserting }

public sealed class Dictation
{
    public Dictation(Guid clientId, DateTimeOffset startedAt, FrontmostApp? app)
    {
        ClientId = clientId;
        StartedAt = startedAt;
        App = app;
    }

    public Guid ClientId { get; }
    public DateTimeOffset StartedAt { get; }
    public FrontmostApp? App { get; set; }
    public DictationStage Stage { get; set; } = DictationStage.Recording;
    public float[] Samples { get; set; } = [];
    public int AudioMs { get; set; }
    public string? Raw { get; set; }
    public string? Language { get; set; }
    public int AsrMs { get; set; }
    public string? Cleaned { get; set; }
    public FallbackReason? Fallback { get; set; }
    public Injected? Injected { get; set; }

    /// Cleanup that did not happen on `/v1/refine`: `"skipped"` when the gate decided the transcript
    /// needed nothing. Left null when the server cleaned it.
    public string? LlmModel { get; set; }

    /// How many confirmed streaming segments produced `Raw`; 1 for a one-pass transcription. Above 1
    /// the skip gate refuses to skip, because skipping is the one path where nothing inspects the text.
    public int StreamedSegments { get; set; } = 1;

    /// What gets pasted: the cleanup when there is one, the raw transcript otherwise.
    public string? TextToInsert => Cleaned ?? Raw;

    /// The wire form of the client id: a lowercase hyphenated UUID, as the Mac sends it.
    public string WireId => ClientId.ToString("D");
}

/// `Skipped` is the gate's verdict: the transcript was already clean, so no cleanup engine ran. It
/// inserts the raw text exactly like `Literal`; they are separate because `Literal` is the user's
/// choice and `Skipped` is ours. Port of `RefineResult` in DictationMachine.swift.
public abstract record RefineResult
{
    private RefineResult() { }
    public sealed record Cleaned(string Text) : RefineResult;
    public sealed record RawFallback(FallbackReason Reason) : RefineResult;
    public sealed record Literal : RefineResult;
    public sealed record Skipped : RefineResult;
}
