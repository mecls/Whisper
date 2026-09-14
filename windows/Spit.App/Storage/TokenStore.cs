using System.ComponentModel;
using Meziantou.Framework.Win32;

namespace Spit.App;

/// The device token, one per server URL, in Windows Credential Manager: the Windows form of the Mac's
/// Keychain item (service `co.miraside.voice`, account = server URL). `LocalMachine` persistence keeps
/// it on this PC, because a roaming profile must never carry a device token to another machine
/// (rules 20, 44). Only Settings › Server's Save and Sign out write or delete it (Mac D6).
public sealed class TokenStore
{
    public const string DefaultTargetPrefix = "co.miraside.voice";

    private readonly string targetPrefix;

    /// `targetPrefix` is overridable so tests write `co.miraside.voice.test:<guid>`, never the real token.
    public TokenStore(string targetPrefix = DefaultTargetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        this.targetPrefix = targetPrefix;
    }

    public string TargetFor(string serverUrl) => $"{targetPrefix}:{serverUrl}";

    /// Null when there is no token, and when Credential Manager can't be read (logged): the UI then says
    /// "Not connected", which is the truth as far as this launch can tell.
    public string? Read(string serverUrl)
    {
        try
        {
            return CredentialManager.ReadCredential(TargetFor(serverUrl))?.Password;
        }
        catch (Win32Exception e)
        {
            Log.Win32Failure("token", "CredRead", e.NativeErrorCode);
            return null;
        }
    }

    /// Throws on failure so the Save button can say so.
    public void Save(string serverUrl, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        try
        {
            CredentialManager.WriteCredential(TargetFor(serverUrl), serverUrl, token, CredentialPersistence.LocalMachine);
        }
        catch (Win32Exception e)
        {
            Log.Win32Failure("token", "CredWrite", e.NativeErrorCode);
            throw;
        }
    }

    /// False when there was no token to delete. Throws when one exists but can't be deleted.
    public bool Delete(string serverUrl)
    {
        try
        {
            return CredentialManager.TryDeleteCredential(TargetFor(serverUrl));
        }
        catch (Win32Exception e)
        {
            Log.Win32Failure("token", "CredDelete", e.NativeErrorCode);
            throw;
        }
    }
}
