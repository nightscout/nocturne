using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// The running release against the newest published one, with what Watchtower will do about it.
/// </summary>
/// <remarks>
/// Releases are read from GitHub at most once per <see cref="ReleaseCheckOptions.CheckInterval"/>,
/// which keeps an instance well inside GitHub's anonymous limit of 60 requests an hour per address.
/// A failed read keeps the last good one and is retried after <see cref="RetryAfterFailure"/>.
/// Watchtower is probed at most once per <see cref="ProbeTtl"/>, since its next check moves each
/// time it runs.
/// </remarks>
public class ReleaseStatusService(
    IReleaseSource releases,
    IAutoUpdateProbe probe,
    RunningRelease running,
    IOptions<ReleaseCheckOptions> options,
    TimeProvider timeProvider,
    ILogger<ReleaseStatusService> logger)
{
    internal static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReleaseNotesDto? _latest;
    private ReleaseNotesDto? _current;
    private DateTimeOffset? _releasesReadAt;
    private AutoUpdateStatusDto? _autoUpdate;
    private DateTimeOffset _autoUpdateReadAt;

    public async Task<ReleaseStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = new ReleaseStatusDto
        {
            CurrentVersion = running.Version,
            IsReleaseBuild = running.IsRelease,
        };

        if (!options.Value.Enabled || !running.IsRelease
            || !ReleaseVersion.TryParse(running.Version, out var currentVersion))
            return status;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_releasesReadAt is not { } readAt || now - readAt >= options.Value.CheckInterval)
                await ReadReleasesAsync(currentVersion, now, cancellationToken);

            if (_autoUpdate is null || now - _autoUpdateReadAt >= ProbeTtl)
            {
                _autoUpdate = await probe.ProbeAsync(cancellationToken);
                _autoUpdateReadAt = now;
            }

            status.Current = _current;
            status.AutoUpdate = _autoUpdate;
            if (_latest is not null && ReleaseVersion.TryParse(_latest.Version, out var latestVersion)
                && latestVersion > currentVersion)
            {
                status.Latest = _latest;
                status.UpdateAvailable = true;
            }

            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReadReleasesAsync(ReleaseVersion currentVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await releases.GetLatestAsync(cancellationToken);
            var current = latest?.Version == currentVersion.ToString()
                ? latest
                : await releases.GetByVersionAsync(currentVersion, cancellationToken);

            _latest = latest;
            _current = current;
            _releasesReadAt = now;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not read releases from GitHub");
            _releasesReadAt = now - options.Value.CheckInterval + RetryAfterFailure;
        }
    }
}
