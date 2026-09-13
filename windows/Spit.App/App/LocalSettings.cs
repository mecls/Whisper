using System.IO;
using Spit.Core;

namespace Spit.App;

/// `ILocalSettings` over `settings.json`: the Windows stand-in for the Mac's `Preferences` globals that
/// refine and sync read and write. The store locks and writes atomically, so the sync timer may call this
/// from the thread pool.
public sealed class LocalSettings : ILocalSettings
{
    private readonly SettingsStore store;

    public LocalSettings(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public string Mode
    {
        get => store.Current.Mode;
        set => store.Update(s => s with { Mode = value });
    }

    public string Language
    {
        get => store.Current.Language;
        set => store.Update(s => s with { Language = value });
    }

    public string Hotkey
    {
        get => store.Current.Hotkey;
        set => store.Update(s => s with { Hotkey = value });
    }
}
