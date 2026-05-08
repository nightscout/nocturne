using System.Text;
using System.Text.RegularExpressions;

namespace Nocturne.Aspire.Scalar;

/// <summary>
/// Builds the custom CSS passed to Scalar via <c>WithCustomCss</c>. Reads
/// the canonical Nocturne theme files from the web app and adapts them
/// for Scalar's <c>--scalar-*</c> variable surface so the docs page
/// shares one source of truth with the rest of the UI.
/// </summary>
public static class NocturneScalarTheme
{
    /// <summary>
    /// Composes the theme CSS. <paramref name="solutionRoot"/> is the
    /// repository root — the same path the apphost computes for bind
    /// mounts. Falls back to a minimal built-in mapping if the source
    /// CSS files cannot be read.
    /// </summary>
    public static string Build(string solutionRoot)
    {
        var themePath = Path.Combine(
            solutionRoot, "src", "Web", "packages", "ui", "src", "theme.css");
        var nocturneThemePath = Path.Combine(
            solutionRoot, "src", "Web", "packages", "ui", "src", "styles", "nocturne-theme.css");

        var sb = new StringBuilder();
        sb.AppendLine(ReadAndAdapt(themePath));
        sb.AppendLine(ReadAndAdapt(nocturneThemePath));
        sb.AppendLine(ScalarMapping);
        return sb.ToString();
    }

    private static string ReadAndAdapt(string path)
    {
        if (!File.Exists(path)) return string.Empty;

        var css = File.ReadAllText(path);

        // Strip Tailwind v4 directives the browser doesn't understand.
        css = StripTailwind.Replace(css, string.Empty);

        // Nocturne toggles dark mode via a .dark class on <html>; Scalar
        // uses .dark-mode. Mirror Nocturne's .dark blocks under .dark-mode
        // so Scalar's switch picks them up. Append rather than rename so
        // any consumer that re-renders this CSS in a Nocturne page (where
        // .dark is the live class) still works.
        css = MirrorDarkClass.Replace(
            css,
            m => m.Value + Environment.NewLine + m.Value.Replace(".dark", ".dark-mode"));

        return css;
    }

    // Match Tailwind-specific at-rules followed by a string/identifier and
    // (optionally) a brace-balanced block. Not a full CSS parser — just
    // enough to neutralise @import, @plugin, @source, @theme,
    // @custom-variant, @apply, @utility.
    private static readonly Regex StripTailwind = new(
        """
        @(?:import|plugin|source|theme|custom-variant|apply|utility)\b
        (?:[^;{]*)
        (?:\{(?:[^{}]|\{[^{}]*\})*\}|;)
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex MirrorDarkClass = new(
        @"\.dark\s*\{(?:[^{}]|\{[^{}]*\})*\}",
        RegexOptions.Compiled);

    // Maps Scalar's variable surface onto Nocturne's tokens. Single source
    // of duplication — kept minimal because the actual colours live in
    // the imported theme files above.
    private const string ScalarMapping = """
        :root, .light-mode {
          --scalar-color-1: var(--foreground);
          --scalar-color-2: var(--muted-foreground);
          --scalar-color-3: var(--muted-foreground);
          --scalar-color-accent: var(--primary);
          --scalar-background-1: var(--background);
          --scalar-background-2: var(--card);
          --scalar-background-3: var(--muted);
          --scalar-background-accent: color-mix(in oklab, var(--primary) 12%, transparent);
          --scalar-border-color: var(--border);
          --scalar-color-green: var(--status-normal);
          --scalar-color-red: var(--destructive);
          --scalar-color-yellow: var(--system-event-warning);
          --scalar-color-orange: var(--status-warning);
          --scalar-button-1: var(--primary);
          --scalar-button-1-color: var(--primary-foreground);
          --scalar-button-1-hover: var(--accent);
          --scalar-radius: calc(var(--radius) - 4px);
          --scalar-radius-lg: var(--radius);
          --scalar-radius-xl: calc(var(--radius) + 4px);
          --scalar-font: "Montserrat", ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
          --scalar-font-code: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
        }
        .dark-mode {
          --scalar-background-accent: color-mix(in oklab, var(--primary) 22%, transparent);
        }
        """;
}
