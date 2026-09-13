namespace Spit.Core.Tests;

/// `ILocalSettings` for tests, with the Mac's `Preferences` defaults and the PC's default hotkey. Counts
/// hotkey writes so a test can prove sync never makes one (rule 46).
public sealed class InMemoryLocalSettings(string mode = "clean", string language = "auto", string hotkey = "rightCtrl") : ILocalSettings
{
    private string _hotkey = hotkey;

    public string Mode { get; set; } = mode;
    public string Language { get; set; } = language;

    public string Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            HotkeyWrites++;
        }
    }

    public int HotkeyWrites { get; private set; }
}
