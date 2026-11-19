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
    public void WithNoDevices_EveryTemplateIsOffered_WithTypicalLifespans()
    {
        var templates = TrackerTemplates.For([]);

        templates.Select(t => t.Id).Should().Equal(
            TrackerTemplates.Sensor, TrackerTemplates.Site, TrackerTemplates.Pod, TrackerTemplates.Reservoir,
            TrackerTemplates.PumpBattery, TrackerTemplates.Appointment, TrackerTemplates.Reminder,
            TrackerTemplates.Custom);
        templates.Single(t => t.Id == TrackerTemplates.Sensor).Definition.LifespanHours.Should().Be(240);
        templates.Single(t => t.Id == TrackerTemplates.Pod).Definition.Category.Should().Be(TrackerCategory.Pod);
    }

    [Fact]
    public void APodPump_IsTheSite_AndLeavesNoReservoirOrBatteryBesideIt()
    {
        var templates = TrackerTemplates.For([Device("omnipod-5")]);

        templates.Select(t => t.Id).Should().Equal(
            TrackerTemplates.Sensor, TrackerTemplates.Pod, TrackerTemplates.Appointment,
            TrackerTemplates.Reminder, TrackerTemplates.Custom);
        var pod = templates.Single(t => t.Id == TrackerTemplates.Pod).Definition;
        pod.DeviceComponent.Should().Be("omnipod-5/pod");
        pod.NotificationThresholds.Should().Contain(t => t.Urgency == NotificationUrgency.Urgent);
    }

    [Fact]
    public void AnAppointment_IsAnEvent_WithNoLifespan_AndAReminderBeforeIt()
    {
        var appointment = TrackerTemplates.For([]).Single(t => t.Id == TrackerTemplates.Appointment).Definition;

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
            var dto = TrackerDefinitionDto.FromEntity(template.Definition);

            dto.Id.Should().BeEmpty();
            dto.NotificationThresholds.Should().OnlyContain(t => t.OffsetMinutes != null, template.Id);
        }
    }
}
