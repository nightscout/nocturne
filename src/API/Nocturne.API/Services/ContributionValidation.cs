using System.Text;
using System.Text.RegularExpressions;
using Nocturne.Core.Models.Translations;

namespace Nocturne.API.Services;

/// <summary>
/// The contributor identity and free-text note carried by every contribution
/// flow (translations, CMS content). Both flows put these values in a commit
/// message and a pull-request body on the upstream repository, reachable from
/// an anonymous relay ingress — so the rules live here once instead of being
/// restated per controller.
/// </summary>
public static partial class ContributionValidation
{
    public const int MaxNameLength = 128;
    public const int MaxEmailLength = 254;
    public const int MaxNoteLength = 2000;
    public const int MaxPathLength = 512;

    // GitHub's username grammar: alphanumeric and single hyphens, no
    // leading/trailing hyphen, max 39 chars. Anchored with \z, not $: in .NET
    // $ also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9](?:-?[A-Za-z0-9]){0,38}\z")]
    public static partial Regex GitHubUsernamePattern();

    // Conservative mailbox shape: the value lands inside a Co-authored-by
    // trailer, so whitespace and angle brackets must be impossible.
    [GeneratedRegex(@"^[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+\z")]
    public static partial Regex EmailPattern();

    public static string? ValidateContributor(ContributionContributorDto contributor, string? note)
    {
        // Control characters or trailer syntax in any of these fields would
        // allow commit-metadata injection.
        if (string.IsNullOrWhiteSpace(contributor.Name)
            || contributor.Name.Length > MaxNameLength
            || contributor.Name.Any(char.IsControl))
            return $"Contributor name is required, must be under {MaxNameLength} characters, and cannot contain control characters";

        if (contributor.GitHubUsername is { Length: > 0 } username
            && !GitHubUsernamePattern().IsMatch(username))
            return "Invalid GitHub username";

        if (contributor.Email is { Length: > 0 } email
            && (email.Length > MaxEmailLength || !EmailPattern().IsMatch(email)))
            return "Invalid contributor email";

        if (note is { } value
            && (value.Length > MaxNoteLength || value.Any(IsDisallowedControlChar)))
            return $"Note must be under {MaxNoteLength} characters and cannot contain control characters";

        return null;
    }

    /// <summary>
    /// Control characters that no contributed text may carry. Line and tab
    /// structure is kept: it survives both the .po escaper and the note
    /// renderer.
    /// </summary>
    public static bool IsDisallowedControlChar(char c) =>
        char.IsControl(c) && c is not '\n' and not '\t' and not '\r';

    /// <summary>
    /// Renders a contributor-supplied display name for a sink GitHub gives
    /// side effects to. The name arrives from an anonymous relay, so
    /// <c>Jane fixes #12 cc @someone</c> would otherwise auto-close an issue
    /// and notify arbitrary users from the upstream PR body and from the
    /// commit message. A commit message is not markdown — a backslash escape
    /// would render literally there — so the reference-carrying characters
    /// are dropped instead of escaped when <paramref name="markdown"/> is
    /// false. The backslash is escaped first so a submitted <c>\</c> cannot
    /// consume the escape that follows it.
    ///
    /// <c>#</c> handling covers <c>#12</c> and <c>owner/repo#12</c>, but
    /// GitHub resolves two further reference forms that carry no <c>#</c> and
    /// no <c>@</c>: the <c>GH-12</c> shorthand and a full issue or pull URL.
    /// Both honour closing keywords, so both are removed outright — a
    /// person's name legitimately contains neither.
    /// </summary>
    public static string RenderName(string name, bool markdown)
    {
        var value = GitHubPrClient.SanitizeMetadata(name);
        value = markdown
            ? value.Replace("\\", "\\\\").Replace("@", "\\@").Replace("#", "\\#").Replace("`", "\\`")
            : new string([.. value.Where(c => c is not '@' and not '#')]);

        // Last, because dropping a "#" above can splice a reference back
        // together ("htt#ps://…", "GH#-1"). Neither pass can recreate the
        // other's target: URL removal only deletes, and separating "GH" from
        // its digits cannot produce a "://".
        value = UrlReference().Replace(value, "");
        return GitHubShorthandReference().Replace(value, "$1 ").Trim();
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlReference();

    /// <summary>
    /// The <c>GH-123</c> shorthand. Only the hyphen is replaced: the autolink
    /// requires <c>GH-</c> immediately followed by a digit, so a space between
    /// them leaves nothing for GitHub to resolve while the name stays readable.
    /// </summary>
    [GeneratedRegex(@"(GH)-(?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubShorthandReference();

    /// <summary>
    /// Renders free-text contributor input inside a fenced code block, where
    /// nothing is interpreted — which covers every markdown vector at once,
    /// provided the note cannot close the fence. So the fence runs one
    /// backtick longer than the longest backtick run in the note.
    /// </summary>
    public static string RenderNoteAsCodeFence(string note)
    {
        var text = StripControlChars(note).ReplaceLineEndings("\n");
        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));

        var sb = new StringBuilder();
        sb.AppendLine(fence);
        foreach (var line in text.Split('\n'))
            sb.AppendLine(line);
        sb.AppendLine(fence);
        return sb.ToString();
    }

    private static int LongestBacktickRun(string value)
    {
        int longest = 0, run = 0;
        foreach (var c in value)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest)
                longest = run;
        }
        return longest;
    }

    private static string StripControlChars(string value) =>
        new([.. value.Where(c => !char.IsControl(c) || c is '\r' or '\n')]);
}
