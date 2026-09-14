using System.Text.Json.Serialization;
using Spit.Core;

namespace Spit.App;

/// The Mac's `Preferences.Key` set with Windows values (build spec §7). The hotkey is `rightCtrl` or
/// `rightAlt` because Windows never sees Fn (rule 27), and `modelFile` names a ggml file where the Mac
/// names a WhisperKit folder (rule 41). Mode and language are an offline cache; the server wins on sync.
public sealed record Settings
{
    public const string HotkeyRightCtrl = "rightCtrl";
    public const string HotkeyRightAlt = "rightAlt";

    public static Settings Defaults { get; } = new();

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; init; } = HotkeyRightCtrl;

    [JsonPropertyName("sounds")]
    public bool Sounds { get; init; } = true;

    [JsonPropertyName("showTextInHUD")]
    public bool ShowTextInHUD { get; init; } = true;

    [JsonPropertyName("modelFile")]
    public string ModelFile { get; init; } = ModelCatalog.DefaultFile;

    [JsonPropertyName("serverURL")]
    public string ServerURL { get; init; } = "https://voice.miraside.co";

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "clean";

    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("onboarded")]
    public bool Onboarded { get; init; }

    /// Permanent screen furniture that can't be turned off is a bug; default on, one click away.
    [JsonPropertyName("showBar")]
    public bool ShowBar { get; init; } = true;

    /// Off by default, as on the Mac: the streamed-versus-one-pass accuracy gate can't be measured yet.
    [JsonPropertyName("liveTranscription")]
    public bool LiveTranscription { get; init; }

    /// A JSON `null` deserialises into these non-nullable strings, and a hotkey this build doesn't know
    /// (a hand edit, a newer build) reads as the default, like `HotkeyChoice(rawValue:) ?? .fn`.
    internal Settings Normalised() => this with
    {
        Hotkey = Hotkey is HotkeyRightCtrl or HotkeyRightAlt ? Hotkey : Defaults.Hotkey,
        ModelFile = string.IsNullOrWhiteSpace(ModelFile) ? Defaults.ModelFile : ModelFile,
        ServerURL = string.IsNullOrWhiteSpace(ServerURL) ? Defaults.ServerURL : ServerURL,
        Mode = string.IsNullOrWhiteSpace(Mode) ? Defaults.Mode : Mode,
        Language = string.IsNullOrWhiteSpace(Language) ? Defaults.Language : Language,
    };
}
