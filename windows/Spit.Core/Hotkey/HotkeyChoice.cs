using System.Text.Json.Serialization;

namespace Spit.Core;

/// Port of mac/Voice/Hotkey/HotkeyChoice.swift with Windows values (prd-spit-mac-windows.md rule 27):
/// Fn does not reach Windows, so Right Ctrl is the default and Right Alt the alternative. The wire and
/// storage values are local to the PC and never sent to the server (rule 46).
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyChoice>))]
public enum HotkeyChoice
{
    [JsonStringEnumMemberName("rightCtrl")] RightCtrl,
    [JsonStringEnumMemberName("rightAlt")] RightAlt,
}

public static class HotkeyChoiceExtensions
{
    public static IReadOnlyList<HotkeyChoice> AllCases { get; } = [HotkeyChoice.RightCtrl, HotkeyChoice.RightAlt];

    public static string Label(this HotkeyChoice choice) => choice switch
    {
        HotkeyChoice.RightAlt => Strings.HotkeyLabelRightAlt,
        _ => Strings.HotkeyLabelRightCtrl,
    };

    /// The Swift `rawValue`: what `settings.json` stores.
    public static string RawValue(this HotkeyChoice choice) => choice switch
    {
        HotkeyChoice.RightAlt => "rightAlt",
        _ => "rightCtrl",
    };

    /// `HotkeyChoice(rawValue:)`: null for anything that is not a Windows value (including the Mac's
    /// `fn`, `rightOption`, `rightCommand`), so the caller falls back to the default.
    public static HotkeyChoice? FromRawValue(string? raw) => raw switch
    {
        "rightCtrl" => HotkeyChoice.RightCtrl,
        "rightAlt" => HotkeyChoice.RightAlt,
        _ => null,
    };
}
