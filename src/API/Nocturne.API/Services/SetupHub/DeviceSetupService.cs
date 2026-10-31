using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub;

/// <summary>
/// A Devices setup write that would undo something the item did not do, or contradict what is on
/// record.
/// </summary>
public sealed class DeviceSetupConflictException(string message) : Exception(message);

/// <summary>
/// The Devices setup item: guesses the patient's CGM, pump and AID algorithm from what is connected
/// and what recent data says about itself, and records only what the owner confirms. Guesses name
/// <see cref="DeviceCatalog"/> entries; the trackers it offers carry the catalogue's wear times.
/// It takes back only the insulins and trackers it added itself (<see cref="SetupHubAdditionEntity"/>).
/// </summary>
public partial class DeviceSetupService(
    INocturneDbContextFactory contexts,
    IConnectorConfigurationRepository connectorConfigurations,
    IPatientDeviceRepository devices,
    IPatientInsulinRepository insulins,
    IDeviceReattributionService reattribution,
    ITrackerRepository trackers,
    ITrackerAlertRuleSyncService trackerRuleSync,
    ITherapySettingsResolver therapySettings,
    ConsumableTrackerWriter trackerWriter,
    TimeProvider time,
    ILogger<DeviceSetupService> logger)
{
    /// <summary>How far back data counts as evidence of what is in use now.</summary>
    internal static readonly TimeSpan EvidenceWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Model names as they appear in connector names, pump status and reading labels, after
    /// <see cref="Normalise"/>. More specific tokens come first: "tslimmobi" is a Mobi, not an X2.
    /// </summary>
    private static readonly (string Token, string CatalogId)[] ModelTokens =
    [
        ("dexcomg715day", "dexcom-g7-15-day"), ("g715day", "dexcom-g7-15-day"),
        ("dexcomg7", "dexcom-g7"), ("g7", "dexcom-g7"),
        ("dexcomg6", "dexcom-g6"), ("g6", "dexcom-g6"),
        ("libre3plus", "libre-3-plus"), ("libre3", "libre-3"), ("libre2plus", "libre-2-plus"), ("libre2", "libre-2"),
        ("guardian4", "medtronic-guardian-4"), ("guardian3", "medtronic-guardian-3"),
        ("omnipod5", "omnipod-5"), ("dash", "omnipod-dash"),
        ("mobi", "tandem-mobi"), ("tslim", "tandem-tslim-x2"),
        ("780g", "medtronic-780g"), ("770g", "medtronic-770g"),
        ("ypso", "ypsopump"),
        ("danai", "dana-i"), ("danars", "dana-rs"),
    ];

    /// <summary>Manufacturer names and brands, keyed to <see cref="DeviceCatalogEntry.Manufacturer"/>.</summary>
    private static readonly (string Token, string Manufacturer)[] ManufacturerTokens =
    [
        ("dexcom", "Dexcom"),
        ("libre", "Abbott"), ("abbott", "Abbott"),
        ("medtronic", "Medtronic"), ("minimed", "Medtronic"), ("carelink", "Medtronic"),
        ("insulet", "Insulet"), ("omnipod", "Insulet"),
        ("tandem", "Tandem"),
        ("ypso", "Ypsomed"), ("mylife", "Ypsomed"),
        ("dana", "SOOIL"), ("sooil", "SOOIL"),
    ];

    private static readonly DeviceCategory[] SlotCategories = [DeviceCategory.CGM, DeviceCategory.InsulinPump];

    public async Task<DeviceSetup> GetAsync(CancellationToken ct)
    {
        var current = (await devices.GetCurrentAsync(ct)).ToList();
        var evidence = await GatherEvidenceAsync(ct);

        var slots = SlotCategories.Select(category =>
        {
            var recorded = current.FirstOrDefault(d => d.DeviceCategory == category);
            var choices = DeviceCatalog.GetByCategory(category).Where(e => e.Pump is not null || e.Cgm is not null).ToList();
            if (recorded is not null)
                return new DeviceSlot(category, recorded, null, false, [], choices);
            var (guess, modelKnown, support) = Guess(category, evidence.Devices);
            return new DeviceSlot(category, null, guess, modelKnown, support, choices);
        }).ToList();

        var currentInsulins = (await insulins.GetCurrentAsync(ct)).ToList();
        var addedInsulins = await AdditionsAsync(SetupHubRecordKind.Insulin, ct);
        var shortList = ShortList();
        var listedIds = shortList.SelectMany(g => g.Formulations).Select(f => f.Id).ToHashSet();

        var choiceGroups = shortList
            .Select(g => new InsulinChoiceGroup(g.Group, g.Formulations.Select(f =>
            {
                var recorded = currentInsulins.FirstOrDefault(i => i.FormulationId == f.Id);
                return new InsulinChoice(f, recorded?.Id, recorded is not null && addedInsulins.Contains(recorded.Id));
            }).ToList()))
            .ToList();

        var takesNoInsulin = currentInsulins.Count == 0 && await TakesNoInsulinAsync(ct);

        return new DeviceSetup(
            slots,
            evidence.Algorithm,
            choiceGroups,
            currentInsulins,
            currentInsulins.Where(i => i.FormulationId is null || !listedIds.Contains(i.FormulationId)).ToList(),
            await ActionTimeAsync(ct),
            takesNoInsulin,
            [.. (await TrackerOffersAsync(current, ct)).Select(o => o.Offer)]);
    }

    /// <summary>The insulin action time in use now, resolved as insulin on board resolves it.</summary>
    public Task<InsulinActionTime> ActionTimeAsync(CancellationToken ct) =>
        therapySettings.GetActionTimeAsync(time.GetUtcNow().ToUnixTimeMilliseconds(), ct: ct);

    /// <summary>
    /// Records a confirmed guess, or the catalogue entry it was swapped for, as the current device of
    /// its category. A device that was current in that category stops being current and ends today,
    /// and the new one starts today, so attribution can tell their readings apart. The AID
    /// algorithm is only what the owner confirmed with the pump, never the guess itself.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a CGM or pump in the catalogue.</exception>
    public async Task ConfirmDeviceAsync(string catalogId, AidAlgorithm? algorithm, CancellationToken ct)
    {
        var entry = DeviceCatalog.GetById(catalogId);
        if (entry is null || !SlotCategories.Contains(entry.Category))
            throw new ArgumentException($"'{catalogId}' is not a CGM or pump in the device catalogue.", nameof(catalogId));

        var replaced = (await devices.GetCurrentAsync(ct)).Where(d => d.DeviceCategory == entry.Category).ToList();
        DateOnly? today = replaced.Count > 0 ? await PatientTodayAsync(ct) : null;
        foreach (var previous in replaced)
        {
            previous.IsCurrent = false;
            previous.EndDate = today;
            await devices.UpdateAsync(previous.Id, previous, WriteOrigin.Live, ct);
        }

        var created = await devices.CreateAsync(new PatientDevice
        {
            DeviceCategory = entry.Category,
            Manufacturer = entry.Manufacturer,
            // Shown after the manufacturer everywhere a device is listed, so "Dexcom G7" is stored as "G7".
            Model = entry.Name.StartsWith(entry.Manufacturer + " ", StringComparison.Ordinal)
                ? entry.Name[(entry.Manufacturer.Length + 1)..]
                : entry.Name,
            CatalogId = entry.Id,
            AidAlgorithm = entry.Category == DeviceCategory.InsulinPump ? algorithm : null,
            StartDate = today,
            IsCurrent = true,
        }, WriteOrigin.Live, ct);
        await reattribution.ReattributeForDeviceAsync(created, ct);
    }

    /// <summary>Today on the patient's clock; device usage windows are the patient's local dates.</summary>
    private async Task<DateOnly> PatientTodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(await therapySettings.GetTimezoneAsync(ct: ct) ?? "", out var found)
            ? found
            : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, zone));
    }

    /// <summary>
    /// Records a catalogue insulin as current, with the catalogue's action profile. A rapid-acting
    /// insulin is used for basal and bolus when a pump is on record, else for bolus. It becomes the
    /// primary insulin for its role unless one already is.
    /// </summary>
    /// <exception cref="ArgumentException">The formulation is not offered on the insulin list.</exception>
    public async Task AddInsulinAsync(string formulationId, CancellationToken ct)
    {
        var (group, formulation) = ShortList()
            .SelectMany(g => g.Formulations.Select(f => (g.Group, Formulation: f)))
            .FirstOrDefault(c => c.Formulation.Id == formulationId);
        if (formulation is null)
            throw new ArgumentException($"'{formulationId}' is not on the insulin list.", nameof(formulationId));

        var current = (await insulins.GetCurrentAsync(ct)).ToList();
        if (current.Any(i => i.FormulationId == formulationId))
            return;

        var role = group == InsulinGroup.LongActing ? InsulinRole.Basal
            : (await devices.GetCurrentAsync(ct)).Any(d => d.DeviceCategory == DeviceCategory.InsulinPump)
                ? InsulinRole.Both
                : InsulinRole.Bolus;

        var created = await insulins.CreateAsync(new PatientInsulin
        {
            InsulinCategory = formulation.Category,
            Name = formulation.Name,
            FormulationId = formulation.Id,
            Dia = formulation.DefaultDia,
            Peak = formulation.DefaultPeak,
            Curve = formulation.Curve,
            Concentration = formulation.Concentration,
            Role = role,
            IsCurrent = true,
            IsPrimary = !current.Any(i => i.IsPrimary && RolesOverlap(i.Role, role)),
        }, WriteOrigin.Live, ct);
        if (created.IsPrimary)
            await insulins.SetPrimaryAsync(created.Id, ct);

        await RecordAdditionAsync(SetupHubRecordKind.Insulin, created.Id, ct);
        await SetTakesNoInsulinFlagAsync(false, ct);
    }

    /// <summary>Takes back an insulin this item added. One that was on record before stays.</summary>
    /// <exception cref="DeviceSetupConflictException">The insulin was not added here.</exception>
    public async Task RemoveInsulinAsync(string formulationId, CancellationToken ct)
    {
        var recorded = (await insulins.GetCurrentAsync(ct)).FirstOrDefault(i => i.FormulationId == formulationId);
        if (recorded is null)
            return;
        if (!(await AdditionsAsync(SetupHubRecordKind.Insulin, ct)).Contains(recorded.Id))
            throw new DeviceSetupConflictException("This insulin was on record before; change it in patient settings.");

        await insulins.DeleteAsync(recorded.Id, WriteOrigin.Live, ct);
        await ForgetAdditionAsync(recorded.Id, ct);
    }

    /// <summary>Answers the insulin question with "none", or takes that answer back.</summary>
    /// <exception cref="DeviceSetupConflictException">"None" while an insulin is on record.</exception>
    public async Task SetTakesNoInsulinAsync(bool takesNoInsulin, CancellationToken ct)
    {
        if (takesNoInsulin && (await insulins.GetCurrentAsync(ct)).Any())
            throw new DeviceSetupConflictException("An insulin is on record; remove it before answering none.");
        await SetTakesNoInsulinFlagAsync(takesNoInsulin, ct);
    }

    /// <summary>
    /// Creates the offered tracker of <paramref name="kind"/> through <see cref="ConsumableTrackerWriter"/>,
    /// with no threshold ladder: the catalogue's wear time as its lifespan, or the kind's typical one
    /// when the catalogue has none, restarting on the matching device events and started at the
    /// latest of them, recording <paramref name="createdBy"/> as its creator. Does nothing when a
    /// tracker already does its job.
    /// </summary>
    /// <remarks>
    /// The check, the create and this item's record of it run in one transaction under the
    /// consumable lock the legacy conversion and the device provisioner take
    /// (<see cref="ITrackerRepository.ExecuteUnderConsumableLockAsync{T}"/>), so none of them adds a
    /// second tracker for a consumable another has just added.
    /// </remarks>
    /// <exception cref="ArgumentException">No tracker of that kind is offered for the recorded devices.</exception>
    public async Task AddTrackerAsync(TrackerOfferKind kind, string name, string createdBy, CancellationToken ct)
    {
        var created = await trackers.ExecuteUnderConsumableLockAsync(
            async (tx, unit, token) =>
            {
                var (offer, spec) = await OfferAsync(kind, token, unit.Context);
                if (offer.State != TrackerOfferState.Off)
                    return null;

                var written = await trackerWriter.CreateAsync(
                    tx, unit, spec with { Name = name }, createdBy, withAlerts: false, token);
                unit.Context.SetupHubAdditions.Add(new SetupHubAdditionEntity
                {
                    Id = Guid.CreateVersion7(),
                    ItemKey = SetupHubItemKey.Devices,
                    RecordKind = SetupHubRecordKind.Tracker,
                    RecordId = written.Definition.Id,
                });
                await unit.Context.SaveChangesAsync(token);
                return written;
            },
            async (tx, attempt, token) => attempt is null || await ConsumableTrackerWriter.LandedAsync(tx, attempt, token),
            ct);
        if (created is null)
            return;

        try
        {
            await trackerWriter.PublishAsync(trackers, created, CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Announcing tracker {DefinitionId} failed", created.Definition.Id);
        }
        catch (DbException ex)
        {
            logger.LogError(ex, "Announcing tracker {DefinitionId} failed", created.Definition.Id);
        }
        catch (RetryLimitExceededException ex)
        {
            logger.LogError(ex, "Announcing tracker {DefinitionId} failed", created.Definition.Id);
        }
    }

    /// <summary>
    /// Deletes a tracker this item created, with its record, in one transaction under the consumable
    /// lock. A tracker the owner had before is never touched.
    /// </summary>
    /// <exception cref="ArgumentException">No tracker of that kind is offered for the recorded devices.</exception>
    /// <exception cref="DeviceSetupConflictException">The tracker was not created here.</exception>
    public async Task RemoveTrackerAsync(TrackerOfferKind kind, CancellationToken ct)
    {
        var removed = await trackers.ExecuteUnderConsumableLockAsync(
            async (tx, unit, token) =>
            {
                var (offer, _) = await OfferAsync(kind, token, unit.Context);
                if (offer.State == TrackerOfferState.Off)
                    return (Guid?)null;
                if (offer.State != TrackerOfferState.AddedHere)
                    throw new DeviceSetupConflictException("This tracker was there before; change it in tracker settings.");

                var id = offer.DefinitionId!.Value;
                await tx.DeleteDefinitionAsync(id, token);
                unit.Context.SetupHubAdditions.RemoveRange(await unit.Context.SetupHubAdditions
                    .Where(a => a.ItemKey == SetupHubItemKey.Devices && a.RecordId == id)
                    .ToListAsync(token));
                await unit.Context.SaveChangesAsync(token);
                return id;
            },
            async (tx, attempt, token) => attempt is not { } id || await tx.GetDefinitionByIdAsync(id, token) is null,
            ct);

        // TrackersController.DeleteDefinition gives the order: the definition, then its rules.
        if (removed is { } definitionId)
            await trackerRuleSync.DeleteRulesForDefinitionAsync(definitionId, CancellationToken.None);
    }

    /// <summary>The offer of <paramref name="kind"/> and the tracker it stands for.</summary>
    /// <param name="db">The caller's transaction context, so the offer reads what it holds.</param>
    private async Task<(TrackerOffer Offer, ConsumableTrackerSpec Spec)> OfferAsync(
        TrackerOfferKind kind, CancellationToken ct, NocturneDbContext? db = null)
    {
        var current = (await devices.GetCurrentAsync(ct)).ToList();
        var offered = (await TrackerOffersAsync(current, ct, db)).FirstOrDefault(o => o.Offer.Kind == kind);
        if (offered.Offer is null)
            throw new ArgumentException($"No {kind} tracker is offered for the recorded devices.", nameof(kind));
        return offered;
    }

    private async Task<bool> TakesNoInsulinAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.PatientRecords.AnyAsync(r => r.TakesNoInsulin, ct);
    }

    private async Task<HashSet<Guid>> AdditionsAsync(
        SetupHubRecordKind kind, CancellationToken ct, NocturneDbContext? within = null)
    {
        await using var owned = within is null ? await contexts.CreateAsync(ct) : null;
        var db = within ?? owned!;
        return (await db.SetupHubAdditions
                .Where(a => a.ItemKey == SetupHubItemKey.Devices && a.RecordKind == kind)
                .Select(a => a.RecordId)
                .ToListAsync(ct))
            .ToHashSet();
    }

    private async Task RecordAdditionAsync(SetupHubRecordKind kind, Guid recordId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        db.SetupHubAdditions.Add(new SetupHubAdditionEntity
        {
            Id = Guid.CreateVersion7(), ItemKey = SetupHubItemKey.Devices, RecordKind = kind, RecordId = recordId,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task ForgetAdditionAsync(Guid recordId, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        db.SetupHubAdditions.RemoveRange(
            await db.SetupHubAdditions.Where(a => a.ItemKey == SetupHubItemKey.Devices && a.RecordId == recordId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private async Task SetTakesNoInsulinFlagAsync(bool value, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var record = await db.PatientRecords.FirstOrDefaultAsync(ct);
        if (record is null)
        {
            if (!value)
                return;
            record = new PatientRecordEntity { Id = Guid.CreateVersion7() };
            db.PatientRecords.Add(record);
        }
        record.TakesNoInsulin = value;
        await db.SaveChangesAsync(ct);
    }

    private static bool RolesOverlap(InsulinRole a, InsulinRole b) =>
        a == b || a == InsulinRole.Both || b == InsulinRole.Both;

    private static IReadOnlyList<(InsulinGroup Group, IReadOnlyList<InsulinFormulation> Formulations)> ShortList()
    {
        // Diluted and custom formulations stay in the full insulin editor; this is the short list.
        var all = InsulinCatalog.GetAll().Where(f => f.Id != "custom" && f.Concentration >= 100).ToList();
        return
        [
            (InsulinGroup.RapidActing, all.Where(f => f.Category == InsulinCategory.RapidActing).ToList()),
            (InsulinGroup.LongActing, all.Where(f => f.Category is InsulinCategory.LongActing or InsulinCategory.UltraLongActing).ToList()),
        ];
    }

    /// <summary>
    /// The trackers offered for <paramref name="current"/>: the CGM's sensor, and a tracker for each
    /// part of the pump with a lifespan, including those registering it does not track. A device
    /// with no catalog entry gets the typical sensor, or infusion set and reservoir.
    /// </summary>
    private async Task<IReadOnlyList<(TrackerOffer Offer, ConsumableTrackerSpec Spec)>> TrackerOffersAsync(
        IReadOnlyList<PatientDevice> current, CancellationToken ct, NocturneDbContext? within = null)
    {
        var offers = new List<(TrackerOfferKind Kind, string Device, bool Typical, ConsumableTrackerSpec Spec)>();

        if (current.FirstOrDefault(d => d.DeviceCategory == DeviceCategory.CGM) is { } cgm)
        {
            var entry = cgm.CatalogId is null ? null : DeviceCatalog.GetById(cgm.CatalogId);
            var name = entry?.Name ?? cgm.Model;
            var typical = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Sensor);
            offers.Add(entry?.Cgm is { } properties
                ? (TrackerOfferKind.Sensor, name, false, typical with { LifespanHours = properties.SensorDurationDays * 24 })
                : (TrackerOfferKind.Sensor, name, true, typical));
        }

        if (current.FirstOrDefault(d => d.DeviceCategory == DeviceCategory.InsulinPump) is { } pump)
        {
            var entry = pump.CatalogId is null ? null : DeviceCatalog.GetById(pump.CatalogId);
            var name = entry?.Name ?? pump.Model;
            if (entry?.Pump is { } properties)
            {
                foreach (var component in properties.Components)
                    if (ConsumableTrackerSpecs.SpecFor(entry, component) is { } spec)
                        offers.Add((OfferKindOf(component.Kind), name, false, spec));
            }
            else
            {
                offers.Add((TrackerOfferKind.InfusionSet, name, true, ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site)
                    with { Triggers = [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange], Ends = null }));
                offers.Add((TrackerOfferKind.Reservoir, name, true, ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Reservoir)));
            }
        }

        if (offers.Count == 0)
            return [];

        var added = await AdditionsAsync(SetupHubRecordKind.Tracker, ct, within);
        await using var owned = within is null ? await contexts.CreateAsync(ct) : null;
        var existing = await (within ?? owned!).TrackerDefinitions.AsNoTracking().ToListAsync(ct);

        return offers.Select(o =>
        {
            var covering = existing.Where(d => LegacyFallbackRules.Follows(d, o.Spec)).ToList();
            var mine = covering.Where(d => added.Contains(d.Id)).Select(d => (Guid?)d.Id).FirstOrDefault();
            var (state, id) = mine is not null
                ? (TrackerOfferState.AddedHere, mine)
                : covering.Count > 0
                    ? (TrackerOfferState.AlreadyTracked, covering[0].Id)
                    : (TrackerOfferState.Off, null);
            var offer = new TrackerOffer(
                o.Kind, o.Device, o.Spec.LifespanHours / 24, o.Spec.LifespanHours % 24, o.Typical, state, id,
                StartsByHand: o.Spec.Triggers.Count == 0);
            return (offer, o.Spec);
        }).ToList();
    }

    private static TrackerOfferKind OfferKindOf(PumpComponentKind kind) => kind switch
    {
        PumpComponentKind.Pod => TrackerOfferKind.Pod,
        PumpComponentKind.InfusionSet => TrackerOfferKind.InfusionSet,
        PumpComponentKind.Reservoir => TrackerOfferKind.Reservoir,
        PumpComponentKind.Base => TrackerOfferKind.Base,
        PumpComponentKind.Controller => TrackerOfferKind.Controller,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private sealed record Evidence(IReadOnlyList<DeviceEvidence> Devices, AlgorithmGuess? Algorithm);

    /// <summary>
    /// What the tenant's enabled connectors and recent data say about the hardware, strongest first:
    /// a reading's device label and the pump's own status can name a model, a connector only a maker.
    /// </summary>
    private async Task<Evidence> GatherEvidenceAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var since = time.GetUtcNow().UtcDateTime - EvidenceWindow;
        var found = new List<DeviceEvidence>();

        var readingLabel = await db.SensorGlucose
            .Where(g => g.Timestamp >= since && g.Device != null)
            .OrderByDescending(g => g.Timestamp)
            .Select(g => g.Device)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(readingLabel))
            found.Add(new(DeviceEvidenceSource.Readings, readingLabel));

        var pump = await db.PumpSnapshots
            .Where(p => p.Timestamp >= since && (p.Manufacturer != null || p.Model != null))
            .OrderByDescending(p => p.Timestamp)
            .Select(p => new { p.Manufacturer, p.Model })
            .FirstOrDefaultAsync(ct);
        if (pump is not null)
            found.Add(new(DeviceEvidenceSource.PumpStatus, $"{pump.Manufacturer} {pump.Model}".Trim()));

        var connectors = await connectorConfigurations.ListAsync(ct);
        found.AddRange(connectors
            .Where(c => ConnectorConfigurationService.GetEnabledFromConfig(c.ConfigurationJson))
            .Select(c => c.ConnectorName)
            .Order()
            .Select(n => new DeviceEvidence(DeviceEvidenceSource.Connector, n)));

        var algorithmName = await db.ApsSnapshots
            .Where(a => a.Timestamp >= since)
            .OrderByDescending(a => a.Timestamp)
            .Select(a => a.AidAlgorithm)
            .FirstOrDefaultAsync(ct);
        AlgorithmGuess? algorithm = Enum.TryParse<AidAlgorithm>(algorithmName, out var parsed)
            && parsed is not (AidAlgorithm.None or AidAlgorithm.Unknown)
                ? new(parsed, [new(DeviceEvidenceSource.AlgorithmStatus, parsed.ToString())])
                : null;

        return new Evidence(found, algorithm);
    }

    /// <summary>
    /// The catalogue entry of <paramref name="category"/> the evidence points at. The first
    /// evidence naming a model wins; failing that, the first naming a manufacturer, which guesses
    /// that manufacturer's first catalogue model. Supporting evidence is everything that agrees.
    /// </summary>
    private static (DeviceCatalogEntry? Guess, bool ModelKnown, IReadOnlyList<DeviceEvidence> Support) Guess(
        DeviceCategory category, IReadOnlyList<DeviceEvidence> evidence)
    {
        var matches = evidence
            .Select(e => (Evidence: e, Model: ModelIn(e.Detail, category), Manufacturer: ManufacturerIn(e.Detail, category)))
            .ToList();

        if (matches.FirstOrDefault(m => m.Model is not null).Model is { } model)
            return (model, true, matches
                .Where(m => m.Model == model || (m.Model is null && m.Manufacturer == model.Manufacturer))
                .Select(m => m.Evidence).ToList());

        if (matches.FirstOrDefault(m => m.Manufacturer is not null).Manufacturer is { } manufacturer)
            return (DeviceCatalog.GetByCategory(category).First(e => e.Manufacturer == manufacturer), false, matches
                .Where(m => m.Manufacturer == manufacturer)
                .Select(m => m.Evidence).ToList());

        return (null, false, []);
    }

    private static DeviceCatalogEntry? ModelIn(string text, DeviceCategory category)
    {
        var normalised = Normalise(text);
        return ModelTokens
            .Where(t => normalised.Contains(t.Token))
            .Select(t => DeviceCatalog.GetById(t.CatalogId))
            .FirstOrDefault(e => e?.Category == category);
    }

    private static string? ManufacturerIn(string text, DeviceCategory category)
    {
        var normalised = Normalise(text);
        return ManufacturerTokens
            .Where(t => normalised.Contains(t.Token))
            .Select(t => t.Manufacturer)
            .FirstOrDefault(m => DeviceCatalog.GetByCategory(category).Any(e => e.Manufacturer == m));
    }

    private static string Normalise(string text) =>
        NonAlphanumeric().Replace(text.ToLowerInvariant().Replace("+", "plus"), "");

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
