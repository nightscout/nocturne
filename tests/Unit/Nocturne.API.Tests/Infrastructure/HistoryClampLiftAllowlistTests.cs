using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// <c>HistoryClampLiftExtensions.WriteWithHistoryClampLiftedAsync</c> runs a statement as an
/// unclamped reader would, so it is limited to the members listed here; its remarks say what a lifted
/// write may reach. A new caller is added here deliberately or not at all.
/// </summary>
/// <remarks>
/// The <c>NocturneDbContext.SaveChangesAsync</c> entry is not one write but all of them: every save
/// by a clamped reader that only inserts runs lifted, wherever it is made. That is safe while an
/// insert can return nothing but the row it wrote, which <see cref="HistoryClampLiftPremiseTests"/>
/// holds: no <c>ON CONFLICT</c> (which would update and return an existing row), no trigger, and no
/// column default but a literal or the clock.
/// </remarks>
[Trait("Category", "Unit")]
public class HistoryClampLiftAllowlistTests
{
    private const string Method = "WriteWithHistoryClampLiftedAsync";

    private static readonly string[] Allowed =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/NocturneDbContext.cs: NocturneDbContext.SaveChangesAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/AuditedBulkDeleteExtensions.cs: AuditedBulkDeleteExtensions.SoftDeleteRowsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.DeriveFlagsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.HiddenRowsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.LiveIdsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.LiveSpanEndsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.SaveHiddenAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/DuplicateGroupPrimaries.cs: DuplicateGroupPrimaries.TouchAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/GlucoseStreamFlags.cs: GlucoseStreamFlags.AfterRowsChangedAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/GlucoseStreamFlags.cs: GlucoseStreamFlags.RecomputeAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/SoftDeleteRestoreExtensions.cs: SoftDeleteRestoreExtensions.FindLiveKeyClashesAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/StateSpanSupersessionExtensions.cs: StateSpanSupersessionExtensions.ReopenSupersededAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/StateSpanSupersessionExtensions.cs: StateSpanSupersessionExtensions.SupersedeByArrivedAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/DndWindowRepository.cs: DndWindowRepository.ClearedBeforeTheClampAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/DndWindowRepository.cs: DndWindowRepository.CreateAsync",
        // Deleting an account erases the notices that name it, which the clamp hides once old or archived. The
        // erased notices go back only to be broadcast to the person who held each.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SubjectRepository.cs: SubjectRepository.ScrubTenantRowsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/TrackerRepository.cs: TrackerRepository.CompleteInstanceAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/DecomposedTreatmentKeys.cs: DecomposedTreatmentKeys.GetSentUpstreamAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/DecomposedTreatmentKeys.cs: DecomposedTreatmentKeys.StampAsync",
        // A keyed upsert finds the row its key holds even when the clamp hides it, since the unique index spans
        // the tenant, and overwrites it with the writer's record. The answer carries only what the writer sent.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/ActivityRecordRepository.cs: ActivityRecordRepository.WriteHealthSeriesAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/ActivityRecordRepository.cs: ActivityRecordRepository.ResolveAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/Health/HealthSeriesRepository.cs: HealthSeriesRepository.UpsertAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SleepSessionRepository.cs: SleepSessionRepository.SaveAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SleepSessionRepository.cs: SleepSessionRepository.UpsertSessionAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/StateSpanRepository.cs: StateSpanRepository.LoadHiddenGoverningSpansAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/StateSpanRepository.cs: StateSpanRepository.UpsertBatchAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SystemEventRepository.cs: SystemEventRepository.HiddenRowsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/SystemEventRepository.cs: SystemEventRepository.SaveBatchAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SyncUpsertRepositoryBase.cs: SyncUpsertRepositoryBase.SplitUpsertsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SyncUpsertRepositoryBase.cs: SyncUpsertRepositoryBase.UpsertAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/V4RepositoryBase.cs: V4RepositoryBase.SplitLegacyIdUpdatesAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/V4RepositoryBase.cs: V4RepositoryBase.UpsertSplit.SaveAsync",
        // The unique legacy-id index spans the tenant, so an insert checks the ids hidden rows hold; the
        // check only decides whether to insert, and returns no row.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/V4RepositoryBase.cs: V4RepositoryBase.BlockingLegacyIdsAsync",
        // A status's extras row and sensor state report are held off a re-send by the correlation id their
        // group's stored rows hold, hidden or not; the check only decides whether to insert, and returns no row.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/DeviceStatusExtrasRepository.cs: DeviceStatusExtrasRepository.BlockingCorrelationIdsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SensorStateReportRepository.cs: SensorStateReportRepository.SplitUpsertsAsync",
        // The meal endpoint's recreation check agrees with the lifted sync-key match it guards; it returns a
        // bool the write's own refusal already gives.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SyncUpsertRepositoryBase.cs: SyncUpsertRepositoryBase.IsRecreationBlockedAsync",
        // The stored times a sync-key upsert moves only key cache clears and never reach the caller.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SyncUpsertRepositoryBase.cs: SyncUpsertRepositoryBase.GetUpsertTargetTimestampsAsync",
        // Each glucose stream's first arrival feeds only replay's internal classification of readings as
        // live arrivals or imported history; no row of it reaches the caller.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/V4/SensorGlucoseRepository.cs: SensorGlucoseRepository.GetStreamFirstArrivalsAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/Deduplication/LinkedRecordTypes.cs: LinkedRecordTypes.LoadRecordInfoAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Services/Deduplication/LinkedRecordTypes.cs: LinkedRecordTypes.UnlinkedAsync",
    ];

    private static readonly IReadOnlyList<string> Callers = Scan();

    [Fact]
    public void OnlyTheAllowlistLiftsTheClamp()
    {
        Callers.Except(Allowed).Should().BeEmpty(
            $"{Method} lifts the member history clamp; allowlist a new caller here deliberately");
    }

    [Fact]
    public void EveryAllowlistEntryStillCallsIt()
    {
        Allowed.Except(Callers).Should().BeEmpty("a stale entry would silently cover a future call in that member");
    }

    private static List<string> Scan()
    {
        var callers = new List<string>();

        foreach (var source in RepoSources.Src)
        {
            if (!source.Text.Contains(Method, StringComparison.Ordinal))
                continue;

            var file = source.Path;
            callers.AddRange(source.Tree.GetRoot()
                .DescendantNodes()
                .Where(node => node switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == Method,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText == Method,
                    IdentifierNameSyntax name => name.Identifier.ValueText == Method
                        && name.Parent is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax),
                    _ => false,
                })
                .Select(EnclosingMember.Of)
                .Distinct()
                .Select(member => $"{file}: {member}"));
        }

        return callers;
    }
}
