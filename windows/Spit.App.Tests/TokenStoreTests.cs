namespace Spit.App.Tests;

/// Rule 44: the token round-trips through Credential Manager under `co.miraside.voice.test:<guid>`,
/// never the real target.
public sealed class TokenStoreTests
{
    [WindowsFact]
    public void SaveReadDelete_RoundTripsUnderATestTarget()
    {
        var store = new TokenStore("co.miraside.voice.test");
        var server = Guid.NewGuid().ToString("D");
        try
        {
            Assert.Equal($"co.miraside.voice.test:{server}", store.TargetFor(server));
            Assert.Null(store.Read(server));

            store.Save(server, "test-token-value");
            Assert.Equal("test-token-value", store.Read(server));

            Assert.True(store.Delete(server));
            Assert.Null(store.Read(server));
            Assert.False(store.Delete(server));
        }
        finally
        {
            store.Delete(server);
        }
    }

    [WindowsFact]
    public void DefaultTarget_MirrorsTheMacKeychainServiceAndAccount()
    {
        Assert.Equal("co.miraside.voice:https://voice.miraside.co", new TokenStore().TargetFor("https://voice.miraside.co"));
    }
}
