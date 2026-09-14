using System.IO;
using System.Media;

namespace Spit.App;

/// The start and stop cues (rule 44): two WAV files shipped beside the exe, loaded once at startup so
/// the first dictation's cue isn't late, and played asynchronously so neither blocks the hotkey path.
/// A missing or unreadable file disables its cue with one log line. The caller checks the Play sounds
/// setting; this class only plays.
public sealed class Sounds : IDisposable
{
    public const string StartFile = "start.wav";
    public const string StopFile = "stop.wav";

    private readonly SoundPlayer? start;
    private readonly SoundPlayer? stop;

    /// `directory` defaults to `<app>\Assets`, where the project copies the WAVs.
    public Sounds(string? directory = null)
    {
        var dir = directory ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        start = Load(Path.Combine(dir, StartFile), out var startProblem);
        stop = Load(Path.Combine(dir, StopFile), out var stopProblem);
        if (startProblem is not null || stopProblem is not null)
            Log.Error("sounds", $"cues disabled: start={startProblem ?? "ok"} stop={stopProblem ?? "ok"}");
    }

    public void PlayStart() => Play(start);

    public void PlayStop() => Play(stop);

    public void Dispose()
    {
        start?.Dispose();
        stop?.Dispose();
    }

    private static SoundPlayer? Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            problem = "missing";
            return null;
        }
        var player = new SoundPlayer(path);
        try
        {
            player.Load();
            return player;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            problem = e.GetType().Name;
            player.Dispose();
            return null;
        }
    }

    private static void Play(SoundPlayer? player)
    {
        if (player is null) return;
        try
        {
            player.Play();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Log.Failure("sounds", "play", e);
        }
    }
}
