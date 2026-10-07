using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ClipboardManager.Core.Updates;

/// <summary>
/// A SemVer 2.0 version as used by the release tags ("v0.10.0", "1.0.0-beta.2"). Build metadata
/// ("+abc1234", appended to the informational version by the SDK) is accepted and ignored, as
/// SemVer precedence requires.
/// </summary>
public sealed record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public static ReleaseVersion Zero { get; } = new(0, 0, 0, string.Empty);

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Dot-separated pre-release identifiers ("beta.2"); empty for a release.</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (!value[(plus + 1)..].Split('.').All(IsIdentifier))
            {
                return false;
            }

            value = value[..plus];
        }

        var prerelease = string.Empty;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (!prerelease.Split('.').All(p => IsIdentifier(p) && (!IsNumeric(p) || HasNoLeadingZero(p))))
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length != 3 || !TryParseNumber(parts[0], out var major) || !TryParseNumber(parts[1], out var minor) || !TryParseNumber(parts[2], out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) < 0;

    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) <= 0;

    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) > 0;

    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => Compare(left, right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPrerelease ? "-" + Prerelease : string.Empty);

    private static int Compare(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    /// <summary>
    /// SemVer §11: a release ranks above its pre-releases; identifiers compare one by one, numbers
    /// numerically and below text, and a longer list wins when all shared identifiers are equal.
    /// </summary>
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length ? 0 : left.Length == 0 ? 1 : -1;
        }

        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = IsNumeric(a[i]);
            var bNumeric = IsNumeric(b[i]);
            var result = (aNumeric, bNumeric) switch
            {
                (true, true) => a[i].Length != b[i].Length ? a[i].Length.CompareTo(b[i].Length) : string.CompareOrdinal(a[i], b[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && IsNumeric(text) && HasNoLeadingZero(text) &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsIdentifier(string text) => text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool IsNumeric(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    private static bool HasNoLeadingZero(string digits) => digits.Length == 1 || digits[0] != '0';
}
