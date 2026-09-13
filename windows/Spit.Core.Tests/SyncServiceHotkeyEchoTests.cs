using System.Text.Json;

namespace Spit.Core.Tests;

/// Windows-only (rule 46, build spec invariant 6 and AC-5): the PC never changes the Mac's hotkey.
public sealed class SyncServiceHotkeyEchoTests
{
    [Fact]
    public async Task testModeChangeEchoesTheMacHotkeyAndLlmModelExactly()
    {
        var api = new StubApi
        {
            MeResult = Result<MeResponse>.Success(
                SyncServiceTests.meResponse("clean", "auto", "rightCommand", llmModel: "gpt-oss:120b")),
        };
        var settings = new InMemoryLocalSettings();
        var s = new SyncService(api, settings);
        await s.SyncAsync();

        await s.PushAsync(mode: "literal");

        var put = Assert.Single(api.Puts);
        Assert.Equal(new ServerSettings("literal", "auto", "rightCommand", "gpt-oss:120b"), put);
        // And on the wire, exactly as the server will read it.
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(put, SpitJson.Options));
        Assert.Equal("rightCommand", body.RootElement.GetProperty("hotkey").GetString());
        Assert.Equal("gpt-oss:120b", body.RootElement.GetProperty("llmModel").GetString());
        Assert.Equal("literal", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("rightCtrl", settings.Hotkey);
    }

    [Fact]
    public async Task testNoPutWithoutASuccessfulMe()
    {
        var api = new StubApi();   // MeResult defaults to an offline failure
        var settings = new InMemoryLocalSettings();
        var s = new SyncService(api, settings);
        await s.SyncAsync();

        await s.PushAsync(mode: "literal");

        Assert.Empty(api.Puts);
        Assert.Equal("literal", settings.Mode);   // the local change still applies
    }
}
