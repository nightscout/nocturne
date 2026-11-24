using FluentAssertions;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerTemplatesTests
{
    private static PatientDevice Device(string catalogId) => new() { CatalogId = catalogId, Rank = 0 };

    [Fact]
    public void EverySpecTheCatalogAndTheGenericKindsCallFor_MakesAValidDefinition()
    {
        var now = DateTime.UtcNow;
        var specs = DeviceCatalog.GetAll().SelectMany(ConsumableTrackerSpecs.SpecsFor)
            .Concat(Enum.GetValues<LegacyConsumableKind>().Select(ConsumableTrackerSpecs.Generic))
            .Append(ConsumableTrackerSpecs.TypicalPod)
            .ToList();

        specs.Should().NotBeEmpty();
        foreach (var spec in specs)
        {
            FluentActions.Invoking(() => ConsumableTrackerSpecs.ToDefinition(spec, string.Empty, now))
                .Should().NotThrow(spec.Name);
            FluentActions.Invoking(() => ConsumableTrackerSpecs.ToDefinition(spec, string.Empty, now, withThresholds: false))
                .Should().NotThrow(spec.Name);
        }
    }

    private static IEnumerable<(string Devices, TrackerTemplate Template)> EveryTemplateForEachDeviceSet() =>
        DeviceCatalog.GetAll().Select(entry => (IReadOnlyCollection<PatientDevice>)[Device(entry.Id)])
            .Prepend([])
            .SelectMany(devices => TrackerTemplates.For(devices)
                .Select(t => (string.Join(",", devices.Select(d => d.CatalogId)), t)));

    private static TrackerDefinitionDraft Named(TrackerDefinitionDraft draft) =>
        string.IsNullOrWhiteSpace(draft.Name) ? draft with { Name = "Named by the person" } : draft;

    [Fact]
    public void EveryTemplate_ForNoDevicesAndForEachCatalogDevice_OnceNamed_MakesAValidDefinition()
    {
        foreach (var (devices, template) in EveryTemplateForEachDeviceSet())
        {
            TrackerDefinition.Create(Named(template.Draft), "owner", DateTime.UtcNow).Error
                .Should().BeNull($"{template.Id} for [{devices}]");
        }
    }

    [Fact]
    public void EveryTemplate_IsServedAsTheDefinitionItsDraftMakes()
    {
        foreach (var (devices, template) in EveryTemplateForEachDeviceSet())
        {
            var now = DateTime.UtcNow;
            var created = TrackerDefinition.Create(Named(template.Draft), "owner", now).Definition!;

            var served = TrackerDefinitionDto.From(template.Draft, now);

            served.Should().BeEquivalentTo(
                TrackerDefinitionDto.From(created),
                o => o.WithStrictOrdering().Excluding(d => d.Name),
                $"{template.Id} for [{devices}]");
            served.Name.Should().Be(template.Draft.Name);
            served.CreatedAt.Should().Be(now);
            served.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        }
    }

    [Theory]
    [InlineData(TrackerTemplates.Appointment)]
    [InlineData(TrackerTemplates.Reminder)]
    [InlineData(TrackerTemplates.Custom)]
    public void ATemplateThePersonNames_IsNoDefinitionUntilNamed(string id)
    {
        var draft = TrackerTemplates.For([]).Single(t => t.Id == id).Draft;

        draft.Name.Should().BeEmpty();
        TrackerDefinition.Create(draft, "owner", DateTime.UtcNow).Error!.Rule.Should().Be(TrackerDefinitionRule.Name);
    }

    [Fact]
    public void WithNoDevices_EveryTemplateIsOffered_WithTypicalLifespans()
    {
        var templates = TrackerTemplates.For([]);

        templates.Select(t => t.Id).Should().Equal(
            TrackerTemplates.Sensor, TrackerTemplates.Site, TrackerTemplates.Pod, TrackerTemplates.Reservoir,
            TrackerTemplates.PumpBattery, TrackerTemplates.Appointment, TrackerTemplates.Reminder,
            TrackerTemplates.Custom);
        templates.Single(t => t.Id == TrackerTemplates.Sensor).Draft.LifespanHours.Should().Be(240);
        templates.Single(t => t.Id == TrackerTemplates.Pod).Draft.Category.Should().Be(TrackerCategory.Pod);
    }

    [Fact]
    public void APodPump_IsTheSite_AndLeavesNoReservoirOrBatteryBesideIt()
    {
        var templates = TrackerTemplates.For([Device("omnipod-5")]);

        templates.Select(t => t.Id).Should().Equal(
            TrackerTemplates.Sensor, TrackerTemplates.Pod, TrackerTemplates.Appointment,
            TrackerTemplates.Reminder, TrackerTemplates.Custom);
        var pod = templates.Single(t => t.Id == TrackerTemplates.Pod).Draft;
        pod.DeviceComponent.Should().Be("omnipod-5/pod");
        pod.NotificationThresholds.Should().Contain(t => t.Urgency == NotificationUrgency.Urgent);
    }

    [Fact]
    public void AnAppointment_IsAnEvent_WithNoLifespan_AndAReminderBeforeIt()
    {
        var appointment = TrackerTemplates.For([]).Single(t => t.Id == TrackerTemplates.Appointment).Draft;

        appointment.Mode.Should().Be(TrackerMode.Event);
        appointment.LifespanHours.Should().BeNull();
        appointment.NotificationThresholds.Should().ContainSingle()
            .Which.Hours.Should().Be(-TrackerTemplates.AppointmentReminderHours);
    }

    [Fact]
    public void EveryTemplate_IsUnsaved_AndEachOfItsThresholdsCanFire()
    {
        foreach (var template in TrackerTemplates.For([]))
        {
            var dto = TrackerDefinitionDto.From(template.Draft, DateTime.UtcNow);

            dto.Id.Should().BeEmpty();
            dto.NotificationThresholds.Should().OnlyContain(t => t.OffsetMinutes != null, template.Id);
        }
    }
}
