using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs <see cref="ISensorSessionInferenceService"/> over an active tenant's
/// <see cref="SensorSessionInferenceService.LiveWindow"/> every <see cref="Interval"/>.
/// </summary>
/// <remarks>
/// The run's scope is system-attributed, as <see cref="SensorSessionInferenceRunner"/> requires of
/// every inference write.
/// </remarks>
internal sealed class SensorSessionInferenceJob : IScheduledJob
{
    internal const string JobName = "sensor-session-inference";

    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.PerTenant;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        if (run.Tenant!.IsActive)
        {
            var now = run.StartedAt.UtcDateTime;
            await run.Services.GetRequiredService<ISensorSessionInferenceService>()
                .InferAsync(now - SensorSessionInferenceService.LiveWindow, now, ct);
        }

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
