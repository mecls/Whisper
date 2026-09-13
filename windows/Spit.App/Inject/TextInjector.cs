using System.IO;
using System.Runtime.InteropServices;
using Spit.Core;

namespace Spit.App;

/// Clipboard paste with a restore that errs late, never early (rule 32). Port of the Mac's
/// `TextInjector`, with Windows' default: the text is placed directly and the user's clipboard comes
/// back at the 1.5 s ceiling, because the first reader of delay-rendered data may be Clipboard History
/// rather than the target, so a read can't be trusted to mean the paste landed.
///
/// Meant to be driven from the UI thread, one dictation at a time, like the Mac's.
public sealed class TextInjector
{
    /// Mac: `postCommandV` runs 30 ms after the pasteboard write.
    public static readonly TimeSpan KeystrokeDelay = TimeSpan.FromMilliseconds(30);

    /// Mac `readCeiling`, measured from the keystroke.
    public static readonly TimeSpan RestoreCeiling = TimeSpan.FromSeconds(1.5);

    private readonly Func<nint> ownerWindow;
    private readonly Lock gate = new();

    // The user's clipboard as it was before the first of any pastes whose restore is still pending, and
    // a counter that lets a pending restore see a newer paste has taken over. Both are read and written
    // only while the clipboard is open, which serialises them against every other clipboard user.
    private ClipboardSnapshot? pendingSnapshot;
    private int generation;
    private Task pendingRestore = Task.CompletedTask;

    /// `ownerWindow` returns Spit's own HWND, which every clipboard open uses (rule 33).
    public TextInjector(Func<nint> ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        this.ownerWindow = ownerWindow;
    }

    /// Spit's executable name in `ForegroundContext`'s form (`spit.exe`).
    public static string OwnExecutable => PasteRouting.OwnBundleId;

    /// `PasteRouting.MustUseClipboard`, with the reason kept so the bar can say which one applied.
    /// Elevation wins for every target, as secure input does on the Mac.
    public static PasteRoute Route(string? targetExe, bool targetElevated)
    {
        if (!PasteRouting.MustUseClipboard(targetExe, targetElevated)) return PasteRoute.Paste;
        return targetElevated ? PasteRoute.ClipboardOnlyElevated : PasteRoute.ClipboardOnlySelf;
    }

    /// Completes when the most recently scheduled restore has run (or stood down). For quitting and tests.
    public Task WaitForRestoreAsync()
    {
        lock (gate) return pendingRestore;
    }

    /// Pastes `text` where the user was typing. `targetExe` is the app captured at hotkey-down, as the
    /// Mac's coordinator passes it; elevation is probed now, on the window that will receive Ctrl+V.
    public Task<InsertResult> InsertAsync(string text, string? targetExe)
    {
        var processId = ForegroundContext.ForegroundProcessId();
        var elevated = processId is { } id && ElevationProbe.IsElevated(id);
        return InsertAsync(text, Route(targetExe, elevated));
    }

    /// Returns once Ctrl+V is sent (about 30 ms); the restore follows on its own at the ceiling.
    public async Task<InsertResult> InsertAsync(string text, PasteRoute route)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (route)
        {
            case PasteRoute.ClipboardOnlySelf:
                return await PlaceWithoutRestoreAsync(text) ? InsertResult.ClipboardOnlySelf : InsertResult.Failed;
            case PasteRoute.ClipboardOnlyElevated:
                return await PlaceWithoutRestoreAsync(text) ? InsertResult.ClipboardOnlyElevated : InsertResult.Failed;
        }

        var mine = 0;
        var placed = await ClipboardSession.RunAsync(ownerWindow(), "paste", () =>
        {
            lock (gate)
            {
                mine = ++generation;
                // A restore still pending from an earlier paste means the clipboard holds that dictation,
                // not the user's content: keep the older snapshot rather than capture our own text.
                pendingSnapshot ??= ClipboardSnapshot.CaptureOpen();
            }
            if (ClipboardSession.PlaceText(text)) return true;

            // Emptied but not written: nothing will be pasted, so putting the user's clipboard back now
            // can't be early.
            ClipboardSnapshot? snapshot;
            lock (gate)
            {
                snapshot = pendingSnapshot;
                pendingSnapshot = null;
            }
            if (snapshot is not null && !snapshot.RestoreOpen()) Log.Error("paste", "restore after a failed write failed");
            return false;
        });
        if (!placed) return InsertResult.Failed;

        await Task.Delay(KeystrokeDelay);

        if (!SendControlV())
        {
            lock (gate)
            {
                // The text stays for the user to paste themselves; a restore would wipe it.
                if (generation == mine) pendingSnapshot = null;
            }
            return InsertResult.ClipboardOnlyKeystrokeFailed;
        }

        var restore = RestoreAfterCeilingAsync(mine);
        lock (gate) pendingRestore = restore;
        return InsertResult.Pasted;
    }

    /// For "Copy last dictation" and the clipboard-only routes: the text replaces the clipboard, excluded
    /// from history, and is not restored away. False when the clipboard stayed busy past 200 ms.
    public bool CopyToClipboard(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ClipboardSession.Run(ownerWindow(), "copy", () => PlaceReplacingPendingPaste(text));
    }

    private Task<bool> PlaceWithoutRestoreAsync(string text) =>
        ClipboardSession.RunAsync(ownerWindow(), "copy", () => PlaceReplacingPendingPaste(text));

    // Runs with the clipboard open. A paste still waiting to restore stands down: its restore would
    // overwrite this text, which is what the user now wants on the clipboard. The Mac restores that
    // snapshot and then overwrites it, which leaves the same result.
    private bool PlaceReplacingPendingPaste(string text)
    {
        lock (gate)
        {
            generation++;
            pendingSnapshot = null;
        }
        return ClipboardSession.PlaceText(text);
    }

    private async Task RestoreAfterCeilingAsync(int mine)
    {
        try
        {
            await Task.Delay(RestoreCeiling);
            var restored = await ClipboardSession.RunAsync(ownerWindow(), "restore", () =>
            {
                ClipboardSnapshot? snapshot;
                lock (gate)
                {
                    if (generation != mine) return true;   // a newer paste or copy owns the clipboard now
                    snapshot = pendingSnapshot;
                    pendingSnapshot = null;
                }
                return snapshot?.RestoreOpen() ?? true;
            });
            // Busy: the snapshot stays pending, so the next paste still restores the user's clipboard
            // rather than capturing this dictation as if it were theirs.
            if (!restored) Log.Error("paste", "clipboard restore did not complete; the dictation is still on the clipboard");
        }
        catch (Exception e)
        {
            Log.Failure("paste", "clipboard restore", e);
        }
    }

    private static unsafe bool SendControlV()
    {
        var inputs = stackalloc Native.INPUT[4];
        inputs[0] = Key(Native.VK_CONTROL, up: false);
        inputs[1] = Key(Native.VK_V, up: false);
        inputs[2] = Key(Native.VK_V, up: true);
        inputs[3] = Key(Native.VK_CONTROL, up: true);

        // SendInput marks these LLKHF_INJECTED, which is how the hotkey interpreter knows they are not
        // "another key" (rule 28).
        var sent = Native.SendInput(4, inputs, sizeof(Native.INPUT));
        if (sent == 4) return true;
        Log.Win32Failure("paste", $"SendInput ({sent} of 4 events)", Marshal.GetLastPInvokeError());
        return false;
    }

    private static Native.INPUT Key(ushort virtualKey, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT { wVk = virtualKey, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 },
        },
    };
}
