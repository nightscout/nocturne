using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Configuration;

/// <summary>
/// Fills <see cref="LoopConfiguration"/> from the legacy Nightscout <c>LOOP_*</c> variables
/// (<see cref="ConnectorEnvironmentVariables"/>) for any value the <c>Loop</c> section leaves empty.
/// </summary>
/// <remarks>
/// Registered after the section binding so it runs second. Emptiness is read from the section rather
/// than the bound property because <see cref="LoopConfiguration.PushServerEnvironment"/> is never empty.
/// </remarks>
public sealed class LoopLegacyEnvironmentConfiguration(IConfiguration configuration)
    : IConfigureOptions<LoopConfiguration>
{
    private const string Section = "Loop";

    public void Configure(LoopConfiguration options)
    {
        options.ApnsKey = Resolve(nameof(LoopConfiguration.ApnsKey), ConnectorEnvironmentVariables.LoopApnsKey) ?? options.ApnsKey;
        options.ApnsKeyId = Resolve(nameof(LoopConfiguration.ApnsKeyId), ConnectorEnvironmentVariables.LoopApnsKeyId) ?? options.ApnsKeyId;
        options.DeveloperTeamId = Resolve(nameof(LoopConfiguration.DeveloperTeamId), ConnectorEnvironmentVariables.LoopDeveloperTeamId) ?? options.DeveloperTeamId;
        options.PushServerEnvironment = Resolve(nameof(LoopConfiguration.PushServerEnvironment), ConnectorEnvironmentVariables.LoopPushServerEnvironment) ?? options.PushServerEnvironment;
    }

    private string? Resolve(string sectionKey, string legacyVariable)
    {
        if (!string.IsNullOrEmpty(configuration[$"{Section}:{sectionKey}"]))
            return null;

        var legacy = configuration[legacyVariable];
        return string.IsNullOrEmpty(legacy) ? null : legacy;
    }
}
