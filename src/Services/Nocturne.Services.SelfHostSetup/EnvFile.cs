using System.Text;

namespace Nocturne.Services.SelfHostSetup;

/// <summary>
/// Sets keys in a Compose <c>.env</c> file, keeping every other line (the first-boot secrets) as
/// written.
/// </summary>
public static class EnvFile
{
    public static string Apply(string content, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (value.Contains('\n') || value.Contains('\r'))
                throw new ArgumentException($"{key} must be a single line.", nameof(values));
        }

        var pending = new Dictionary<string, string>(values);
        var lines = content.Split('\n');
        var hasTrailingNewline = content.EndsWith('\n');
        var lineCount = hasTrailingNewline ? lines.Length - 1 : lines.Length;
        var result = new StringBuilder();

        for (var i = 0; i < lineCount; i++)
        {
            var line = lines[i];
            var separator = line.IndexOf('=');
            var key = separator > 0 ? line[..separator] : null;
            if (key is not null && pending.Remove(key, out var replacement))
                line = $"{key}={replacement}";
            result.Append(line).Append('\n');
        }

        foreach (var (key, value) in values)
        {
            if (pending.ContainsKey(key))
                result.Append(key).Append('=').Append(value).Append('\n');
        }

        return result.ToString();
    }
}
