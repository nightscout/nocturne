namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// A <c>major.minor.patch[-pre-release]</c> version, as release tags and
/// <see cref="AppVersion.Current"/> carry it. A pre-release sorts before its release.
/// </summary>
public readonly record struct ReleaseVersion(Version Core, string? PreRelease) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        var pre = dash < 0 ? null : text[(dash + 1)..];

        if (!Version.TryParse(core, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
            return false;

        version = new ReleaseVersion(parsed, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var byCore = Core.CompareTo(other.Core);
        if (byCore != 0)
            return byCore;

        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePreRelease(PreRelease!, other.PreRelease!),
        };
    }

    // SemVer precedence: dot-separated identifiers compared in turn, numeric ones by value and
    // below alphanumeric ones, and a shorter list first when it is a prefix of the longer.
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = ulong.TryParse(a[i], out var aNumber);
            var bNumeric = ulong.TryParse(b[i], out var bNumber);
            var byIdentifier = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (byIdentifier != 0)
                return byIdentifier;
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public override string ToString() => PreRelease is null ? Core.ToString() : $"{Core}-{PreRelease}";
}
