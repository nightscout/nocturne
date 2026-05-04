using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Nocturne.API.Tests.GoldenFiles.Infrastructure;

/// <summary>
/// Global Verify configuration. Module initializer runs automatically before any tests.
/// </summary>
public static class VerifyConfig
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Disable diff tool launching — DiffEngine's WMI-based ProcessCleanup
        // fails on some Windows environments, causing all Verify tests to throw
        // TypeInitializationException.
        Environment.SetEnvironmentVariable("DiffEngine_Disabled", "true");

        // Scrub dynamic values that change between runs
        VerifierSettings.ScrubMembers("serverTime", "serverTimeEpoch", "srvDate", "head", "uptimeMs");

        // Scrub traceId from problem+json error responses (appears in raw JSON body strings)
        VerifierSettings.AddScrubber(builder =>
        {
            var text = builder.ToString();
            text = Regex.Replace(text, @"""traceId""\s*:\s*""[^""]*""", @"""traceId"": ""{scrubbed}""");
            builder.Clear();
            builder.Append(text);
        });
    }
}
