using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

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
    NocturneDbContext db,
    IPatientDeviceRepository devices,
    IPatientInsulinRepository insulins,
    IDeviceReattributionService reattribution,
    ITrackerRepository trackers,
    ITrackerAlertRuleSyncService trackerRuleSync)
{
    /// <summary>How far back data counts as evidence of what is in use now.</summary>
    internal static readonly TimeSpan EvidenceWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Model names as they appear in connector names, pump status and reading labels, after
    /// <see cref="Normalise"/>. More specific tokens come first: "tslimmobi" is a Mobi, not an X2.
    /// </summary>
    private static readonly (string Token, string CatalogId)[] ModelTokens =
    [
        ("dexcomg7", "dexcom-g7"), ("g7", "dexcom-g7"),
        ("dexcomg6", "dexcom-g6"), ("g6", "dexcom-g6"),
        ("libre3", "libre-3"), ("libre2plus", "libre-2-plus"), ("libre2", "libre-2"),
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

    public async Task<DeviceSetup> GetAsync(string userId, CancellationToken ct)
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

        var takesNoInsulin = currentInsulins.Count == 0
            && await db.PatientRecords.AnyAsync(r => r.TakesNoInsulin, ct);

        return new DeviceSetup(
            slots,
            evidence.Algorithm,
            choiceGroups,
            currentInsulins,
            currentInsulins.Where(i => i.FormulationId is null || !listedIds.Contains(i.FormulationId)).ToList(),
            currentInsulins.FirstOrDefault(i => i.IsPrimary && i.Role is InsulinRole.Bolus or InsulinRole.Both),
            takesNoInsulin,
            await TrackerOffersAsync(current, userId, ct));
    }

    /// <summary>
    /// Records a confirmed guess, or the catalogue entry it was swapped for, as the current device of
    /// its category; a device that was current in that category stops being current. The AID
    /// algorithm is only what the owner confirmed with the pump, never the guess itself.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a CGM or pump in the catalogue.</exception>
    public async Task ConfirmDeviceAsync(string catalogId, AidAlgorithm? algorithm, CancellationToken ct)
    {
        var entry = DeviceCatalog.GetById(catalogId);
        if (entry is null || !SlotCategories.Contains(entry.Category))
            throw new ArgumentException($"'{catalogId}' is not a CGM or pump in the device catalogue.", nameof(catalogId));

        foreach (var previous in (await devices.GetCurrentAsync(ct)).Where(d => d.DeviceCategory == entry.Category))
        {
            previous.IsCurrent = false;
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
            IsCurrent = true,
        }, WriteOrigin.Live, ct);
        await reattribution.ReattributeForDeviceAsync(created, ct);
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
    /// Creates the offered tracker of <paramref name="kind"/> for <paramref name="userId"/>, with the
    /// catalogue's wear time as its lifespan and restarting on the matching device events. Does
    /// nothing when the owner already has one.
    /// </summary>
    /// <exception cref="ArgumentException">No tracker of that kind is offered for the recorded devices.</exception>
    public async Task AddTrackerAsync(TrackerOfferKind kind, string name, string userId, CancellationToken ct)
    {
        var (offer, hours) = await OfferAsync(kind, userId, ct);
        if (offer.State != TrackerOfferState.Off)
            return;

        var created = await trackers.CreateDefinitionAsync(new TrackerDefinitionEntity
        {
            UserId = userId,
            Name = name,
            Category = TrackerCategoryOf(kind),
            LifespanHours = hours,
            TriggerEventTypes = JsonSerializer.Serialize(TriggersOf(kind)),
            Mode = TrackerMode.Duration,
        }, ct);
        await trackerRuleSync.SyncDefinitionAsync(created.Id, CancellationToken.None);
        await RecordAdditionAsync(SetupHubRecordKind.Tracker, created.Id, ct);
    }

    /// <summary>Deletes a tracker this item created. A tracker the owner had before is never touched.</summary>
    /// <exception cref="ArgumentException">No tracker of that kind is offered for the recorded devices.</exception>
    /// <exception cref="DeviceSetupConflictException">The tracker was not created here.</exception>
    public async Task RemoveTrackerAsync(TrackerOfferKind kind, string userId, CancellationToken ct)
    {
        var (offer, _) = await OfferAsync(kind, userId, ct);
        if (offer.State == TrackerOfferState.Off)
            return;
        if (offer.State != TrackerOfferState.AddedHere)
            throw new DeviceSetupConflictException("This tracker was there before; change it in tracker settings.");

        var id = offer.DefinitionId!.Value;
        await trackers.DeleteDefinitionAsync(id, ct);
        await trackerRuleSync.DeleteRulesForDefinitionAsync(id, CancellationToken.None);
        await ForgetAdditionAsync(id, ct);
    }

    private async Task<(TrackerOffer Offer, int? Hours)> OfferAsync(TrackerOfferKind kind, string userId, CancellationToken ct)
    {
        var current = (await devices.GetCurrentAsync(ct)).ToList();
        var offer = (await TrackerOffersAsync(current, userId, ct)).FirstOrDefault(o => o.Kind == kind)
            ?? throw new ArgumentException($"No {kind} tracker is offered for the recorded devices.", nameof(kind));
        return (offer, offer.WearDays * 24 + offer.WearHours);
    }

    private async Task<HashSet<Guid>> AdditionsAsync(SetupHubRecordKind kind, CancellationToken ct) =>
        (await db.SetupHubAdditions
            .Where(a => a.ItemKey == SetupHubItemKey.Devices && a.RecordKind == kind)
            .Select(a => a.RecordId)
            .ToListAsync(ct))
        .ToHashSet();

    private async Task RecordAdditionAsync(SetupHubRecordKind kind, Guid recordId, CancellationToken ct)
    {
        db.SetupHubAdditions.Add(new SetupHubAdditionEntity
        {
            Id = Guid.CreateVersion7(), ItemKey = SetupHubItemKey.Devices, RecordKind = kind, RecordId = recordId,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task ForgetAdditionAsync(Guid recordId, CancellationToken ct)
    {
        db.SetupHubAdditions.RemoveRange(
            await db.SetupHubAdditions.Where(a => a.ItemKey == SetupHubItemKey.Devices && a.RecordId == recordId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    private async Task SetTakesNoInsulinFlagAsync(bool value, CancellationToken ct)
    {
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

    private async Task<IReadOnlyList<TrackerOffer>> TrackerOffersAsync(
        IReadOnlyList<PatientDevice> current, string userId, CancellationToken ct)
    {
        var offers = new List<(TrackerOfferKind Kind, string Device, int? Hours)>();

        if (current.FirstOrDefault(d => d.DeviceCategory == DeviceCategory.CGM) is { } cgm)
        {
            var entry = cgm.CatalogId is null ? null : DeviceCatalog.GetById(cgm.CatalogId);
            offers.Add((TrackerOfferKind.Sensor, entry?.Name ?? cgm.Model, entry?.Cgm?.SensorDurationDays * 24));
        }

        if (current.FirstOrDefault(d => d.DeviceCategory == DeviceCategory.InsulinPump) is { } pump)
        {
            var entry = pump.CatalogId is null ? null : DeviceCatalog.GetById(pump.CatalogId);
            var name = entry?.Name ?? pump.Model;
            if (entry?.Pump is { IsPatchPump: true } patch)
            {
                offers.Add((TrackerOfferKind.Pod, name, patch.SiteDurationHours));
            }
            else
            {
                offers.Add((TrackerOfferKind.InfusionSet, name, entry?.Pump?.SiteDurationHours));
                offers.Add((TrackerOfferKind.Reservoir, name, entry?.Pump?.ReservoirDurationHours));
            }
        }

        if (offers.Count == 0)
            return [];

        var added = await AdditionsAsync(SetupHubRecordKind.Tracker, ct);
        var existing = (await db.TrackerDefinitions
                .Where(d => d.UserId == userId)
                .Select(d => new { d.Id, d.Category, d.TriggerEventTypes })
                .ToListAsync(ct))
            .Select(d => (d.Id, d.Category, Triggers: ParseTriggers(d.TriggerEventTypes)))
            .ToList();

        return offers.Select(o =>
        {
            var covering = existing.Where(d => Covers(o.Kind, d.Category, d.Triggers)).ToList();
            var mine = covering.Where(d => added.Contains(d.Id)).Select(d => (Guid?)d.Id).FirstOrDefault();
            var (state, id) = mine is not null
                ? (TrackerOfferState.AddedHere, mine)
                : covering.Count > 0
                    ? (TrackerOfferState.AlreadyTracked, covering[0].Id)
                    : (TrackerOfferState.Off, null);
            return new TrackerOffer(o.Kind, o.Device, o.Hours / 24, o.Hours % 24, state, id);
        }).ToList();
    }

    /// <summary>
    /// Whether an existing tracker already follows what an offer of <paramref name="kind"/> would.
    /// A site tracker restarting on pod changes is a pod tracker, one restarting on anything else
    /// an infusion-set tracker, and one with no triggers could be either.
    /// </summary>
    private static bool Covers(TrackerOfferKind kind, TrackerCategory category, IReadOnlyCollection<string> triggers) => kind switch
    {
        TrackerOfferKind.Sensor => category == TrackerCategory.Sensor,
        TrackerOfferKind.Reservoir => category == TrackerCategory.Reservoir,
        TrackerOfferKind.Pod => category == TrackerCategory.Cannula
            && (triggers.Count == 0 || triggers.Contains(TreatmentTypes.PodChange)),
        _ => category == TrackerCategory.Cannula && !triggers.Contains(TreatmentTypes.PodChange),
    };

    private static IReadOnlyCollection<string> ParseTriggers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TrackerCategory TrackerCategoryOf(TrackerOfferKind kind) => kind switch
    {
        TrackerOfferKind.Sensor => TrackerCategory.Sensor,
        TrackerOfferKind.Reservoir => TrackerCategory.Reservoir,
        _ => TrackerCategory.Cannula,
    };

    private static string[] TriggersOf(TrackerOfferKind kind) => kind switch
    {
        TrackerOfferKind.Sensor => [TreatmentTypes.SensorStart, TreatmentTypes.SensorChange],
        TrackerOfferKind.Pod => [TreatmentTypes.PodChange, TreatmentTypes.SiteChange],
        TrackerOfferKind.InfusionSet => [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange],
        _ => [TreatmentTypes.InsulinChange, TreatmentTypes.ReservoirChangeEvent],
    };

    private sealed record Evidence(IReadOnlyList<DeviceEvidence> Devices, AlgorithmGuess? Algorithm);

    /// <summary>
    /// What the tenant's enabled connectors and recent data say about the hardware, strongest first:
    /// a reading's device label and the pump's own status can name a model, a connector only a maker.
    /// </summary>
    private async Task<Evidence> GatherEvidenceAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - EvidenceWindow;
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

        var connectors = await db.ConnectorConfigurations
            .Select(c => new { c.ConnectorName, c.ConfigurationJson })
            .ToListAsync(ct);
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
