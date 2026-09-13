namespace Spit.Core;

/// Rule 43: `GET /v1/insights?tz=` takes an IANA name and rejects a Windows id such as
/// `GMT Standard Time` with a 400. There is deliberately no UTC fallback — bucketing days in the wrong
/// zone produces a plausible wrong streak that nobody would catch (docs/API.md, insights note).
public static class IanaTimeZone
{
    /// <returns>True with the zone's IANA name; false with an empty string when it has none.</returns>
    public static bool TryGetIana(TimeZoneInfo tz, out string iana)
    {
        var id = tz.Id;

        // Already IANA: every zone on macOS/Linux, and on Windows one found by IANA id. `HasIanaId`, not a
        // '/' test — this Mac's local zone is `Portugal`, a valid IANA link with no slash.
        if (tz.HasIanaId && id.Length > 0)
        {
            iana = id;
            return true;
        }
        if ((id.Contains('/') || id == "UTC" || id.StartsWith("Etc/", StringComparison.Ordinal))
            && TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        {
            iana = id;
            return true;
        }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var converted) && !string.IsNullOrEmpty(converted))
        {
            iana = converted;
            return true;
        }

        iana = string.Empty;
        return false;
    }
}
