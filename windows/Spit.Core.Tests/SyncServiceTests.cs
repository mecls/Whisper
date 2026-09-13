using System.Text.Json;

namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/SyncServiceTests.swift. The Mac snapshots and restores UserDefaults around each
/// test; here every test gets its own `InMemoryLocalSettings`, so there is nothing to restore.
public sealed class SyncServiceTests
{
    /// Builds a `MeResponse` by decoding a literal payload shaped like the real wire format, through the
    /// same serializer options the HTTP client uses.
    internal static MeResponse meResponse(string mode, string language, string hotkey, string? llmModel = null)
    {
        var llmModelJson = llmModel is null ? "null" : $"\"{llmModel}\"";
        var json = $$"""
        {
          "user": {"id": "u1", "name": "Miguel"},
          "settings": {"mode": "{{mode}}", "language": "{{language}}", "hotkey": "{{hotkey}}", "llmModel": {{llmModelJson}}},
          "dictionary": [],
          "server": {"version": "0.1.0", "model": "gpt-oss:120b", "concurrency": 1}
        }
        """;
        return JsonSerializer.Deserialize<MeResponse>(json, SpitJson.Options)!;
    }

    // Mac G1: a server-driven hotkey change goes through a callback, never a direct Preferences write.
    // Windows (rule 46) goes one step further — the server's hotkey is the Mac's and is never applied at
    // all, so there is no callback: the local key survives a sync untouched and unwritten.
    [Fact]
    public async Task testServerHotkeyChangeGoesThroughCallback()
    {
        var settings = new InMemoryLocalSettings(hotkey: "rightCtrl");
        var api = new StubApi { MeResult = Result<MeResponse>.Success(meResponse("clean", "auto", "rightOption")) };
        var s = new SyncService(api, settings);

        await s.SyncAsync();

        Assert.Equal("Miguel", s.UserName);           // the sync itself succeeded
        Assert.Equal("rightCtrl", settings.Hotkey);   // SyncService never wrote it
        Assert.Equal(0, settings.HotkeyWrites);
    }

    // G3(a): push must not clear a server-set llmModel it doesn't know about.
    [Fact]
    public async Task testPushPreservesServerLlmModelOnMerge()
    {
        var api = new StubApi { MeResult = Result<MeResponse>.Success(meResponse("clean", "auto", "fn", llmModel: "gpt-oss:120b")) };
        var s = new SyncService(api, new InMemoryLocalSettings());
        await s.SyncAsync();

        await s.PushAsync(mode: "literal");

        var put = Assert.Single(api.Puts);
        Assert.Equal("gpt-oss:120b", put.LlmModel);
        Assert.Equal("literal", put.Mode);
    }

    // G3(b): pushing back the value sync just wrote is a no-op — kills the settings UI's change echo.
    [Fact]
    public async Task testPushIsNoOpWhenUnchanged()
    {
        var api = new StubApi { MeResult = Result<MeResponse>.Success(meResponse("clean", "auto", "fn")) };
        var s = new SyncService(api, new InMemoryLocalSettings());
        await s.SyncAsync();

        await s.PushAsync(mode: "clean");

        Assert.Empty(api.Puts);
    }
}
