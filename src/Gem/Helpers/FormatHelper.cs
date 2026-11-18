using System.Globalization;
using Gem.Services;

namespace Gem.Helpers;

public static class FormatHelper
{
    private static readonly string[] SizeUnitKeys = ["UnitB", "UnitKB", "UnitMB", "UnitGB", "UnitTB"];

    public static string FormatBytes(long bytes)
    {
        var loc = LocalizationService.Instance;
        if (bytes <= 0)
            return $"0 {loc[SizeUnitKeys[0]]}";

        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < SizeUnitKeys.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {loc[SizeUnitKeys[0]]}"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, loc[SizeUnitKeys[unitIndex]]);
    }

    public static string FormatSpeed(long bytesPerSecond)
    {
        var loc = LocalizationService.Instance;
        if (bytesPerSecond <= 0)
            return $"0 {loc[SizeUnitKeys[0]]}{loc["UnitPerSec"]}";

        return $"{FormatBytes(bytesPerSecond)}{loc["UnitPerSec"]}";
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta.Value <= TimeSpan.Zero || eta.Value.TotalDays > 365)
            return "∞";

        var loc = LocalizationService.Instance;
        var t = eta.Value;
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays}{loc["UnitDayShort"]} {t.Hours}{loc["UnitHourShort"]}";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}{loc["UnitHourShort"]} {t.Minutes}{loc["UnitMinShort"]}";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes}{loc["UnitMinShort"]} {t.Seconds}{loc["UnitSecShort"]}";

        return $"{Math.Max(1, t.Seconds)}{loc["UnitSecShort"]}";
    }

    public static bool IsMagnetUri(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        return trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeToMagnetUri(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string trimmed = input.Trim();
        if (IsMagnetUri(trimmed))
            return trimmed;

        if (trimmed.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["urn:btih:".Length..].Trim();
        else if (trimmed.StartsWith("btih:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["btih:".Length..].Trim();

        if (IsHexString(trimmed, 40))
            return $"magnet:?xt=urn:btih:{trimmed.ToUpperInvariant()}";

        if (IsBase32String(trimmed, 32))
            return $"magnet:?xt=urn:btih:{trimmed.ToUpperInvariant()}";

        if (IsHexString(trimmed, 64))
            return $"magnet:?xt=urn:btmh:1220{trimmed.ToLowerInvariant()}";

        return null;
    }

    private static bool IsHexString(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
            return false;

        foreach (char c in value)
        {
            bool isHex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
            if (!isHex)
                return false;
        }
        return true;
    }

    private static bool IsBase32String(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
            return false;

        foreach (char c in value)
        {
            bool isBase32 = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '2' and <= '7');
            if (!isBase32)
                return false;
        }
        return true;
    }

    public static TimeSpan? CalculateEta(long totalBytes, long downloadedBytes, long downloadSpeedBytesPerSec)
    {
        if (downloadSpeedBytesPerSec <= 0 || totalBytes <= 0)
            return null;

        long remaining = totalBytes - downloadedBytes;
        if (remaining <= 0)
            return TimeSpan.Zero;

        double seconds = (double)remaining / downloadSpeedBytesPerSec;
        if (seconds > TimeSpan.FromDays(365).TotalSeconds)
            return null;

        return TimeSpan.FromSeconds(seconds);
    }

    public static string FormatPeerEndpoint(Uri? uri)
    {
        if (uri is null)
            return string.Empty;

        if (!string.IsNullOrEmpty(uri.Host))
            return uri.Port > 0 ? $"{uri.Host}:{uri.Port}" : uri.Host;

        return FormatPeerEndpoint(uri.ToString());
    }

    public static string FormatPeerEndpoint(string? rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
            return string.Empty;

        string text = rawEndpoint.Trim();
        int schemeIdx = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            text = text[(schemeIdx + 3)..];

        return text.Trim('/');
    }

    public static string? TryNormalizeTrackerUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;

        bool validScheme = uri.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase) ||
                           uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                           uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        return validScheme && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString()
            : null;
    }

    public static List<string> ParseTrackerList(string? multilineText)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(multilineText))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = multilineText.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            string? normalized = TryNormalizeTrackerUri(trimmed);
            if (normalized is not null && seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }
}
