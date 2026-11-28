using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The insulin and carbs on board of the <c>current_status</c> dataset.</summary>
[Trait("Category", "Unit")]
public class CurrentStatusOnBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlySet<string> Treatments = new HashSet<string> { Scope.GlucoseRead, Scope.TreatmentsRead };

    private static readonly IReadOnlySet<string> NoTreatments = new HashSet<string> { Scope.GlucoseRead };

    /// <summary>Totals a device gave: 1.2349 units, which rounds to 1.23, and 12.5 grams, which rounds to 12.</summary>
    private static IobCobReading Reported(double units = 1.2349, double grams = 12.5) => new(
        new IobResult { Iob = units, ReportedByDevice = true },
        new CobResult { Cob = grams, ReportedByDevice = true },
        new IobCobInputs([], [], []));

    /// <summary>Totals with nothing behind them: no device, and no treatment in the window.</summary>
    private static IobCobReading Unknown() => new(new IobResult(), new CobResult(), new IobCobInputs([], [], []));

    [Fact]
    public async Task The_row_gives_the_insulin_and_carbs_on_board_rounded_to_hundredths_and_whole_grams()
    {
        var (output, _) = await RunAsync(Reported(), Treatments);

        var row = output.Rows.Should().ContainSingle().Subject;
        row[CurrentStatusDataset.InsulinOnBoard].Should().Be(1.23);
        row[CurrentStatusDataset.CarbsOnBoard].Should().Be(12d);
        output.Facts.Should().Contain(f => f.Label == "Insulin on board" && f.Value == "1.23" && f.Unit == "U");
        output.Facts.Should().Contain(f => f.Label == "Carbs on board" && f.Value == "12" && f.Unit == "g");
        output.Summary.Should().Contain("Insulin on board: 1.23 U.").And.Contain("Carbs on board: 12 g.");
        output.Omitted.Should().NotContain(o => o.Field == CurrentStatusDataset.InsulinOnBoard);
    }

    [Fact]
    public async Task The_reader_is_asked_for_a_live_total_at_the_request_instant()
    {
        var (_, reader) = await RunAsync(Reported(), Treatments);

        reader.Verify(r => r.ReadTotalAsync(Now.ToUnixTimeMilliseconds(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_total_that_is_not_known_is_no_data_and_never_zero()
    {
        var (output, _) = await RunAsync(Unknown(), Treatments);

        var row = output.Rows.Single();
        row.Should().ContainKey(CurrentStatusDataset.InsulinOnBoard).WhoseValue.Should().BeNull();
        row.Should().ContainKey(CurrentStatusDataset.CarbsOnBoard).WhoseValue.Should().BeNull();
        output.Facts.Should().NotContain(f => f.Label.EndsWith("on board", StringComparison.Ordinal));
        output.Summary.Should().Contain("Insulin on board: not known.").And.Contain("Carbs on board: not known.");
    }

    [Fact]
    public async Task A_zero_total_a_device_gave_is_a_figure()
    {
        var (output, _) = await RunAsync(Reported(units: 0, grams: 0), Treatments);

        var row = output.Rows.Single();
        row[CurrentStatusDataset.InsulinOnBoard].Should().Be(0d);
        row[CurrentStatusDataset.CarbsOnBoard].Should().Be(0d);
    }

    [Fact]
    public async Task Without_treatments_read_the_fields_are_omitted_and_nothing_is_read()
    {
        var (output, reader) = await RunAsync(Reported(), NoTreatments);

        var row = output.Rows.Single();
        row.Should().NotContainKeys(CurrentStatusDataset.OnBoardFields);
        output.Omitted.Should().Contain(
            CurrentStatusDataset.OnBoardFields.Select(f => new ReportOmittedField(f, Scope.TreatmentsRead)));
        output.Summary.Should().Contain("insulinOnBoard, carbsOnBoard");
        reader.Verify(
            r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Both_figures_are_live_flag_facts_in_their_units_under_treatments_read()
    {
        var fields = new CurrentStatusDataset().Fields.Where(f => CurrentStatusDataset.OnBoardFields.Contains(f.Name)).ToList();

        fields.Select(f => (f.Name, f.Unit, f.Scope, f.FlagFact?.Kind)).Should().Equal(
            (CurrentStatusDataset.InsulinOnBoard, "U", Scope.TreatmentsRead, ReportFlagFactKind.Live),
            (CurrentStatusDataset.CarbsOnBoard, "g", Scope.TreatmentsRead, ReportFlagFactKind.Live));
    }

    private static async Task<(ReportDatasetOutput Output, Mock<IIobCobReader> Reader)> RunAsync(
        IobCobReading reading, IReadOnlySet<string> scopes)
    {
        var reader = new Mock<IIobCobReader>();
        reader.Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reading);
        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((SensorGlucose?)null);
        var services = new ServiceCollection()
            .AddSingleton(reader.Object)
            .AddSingleton(canonical.Object)
            .AddSingleton(Mock.Of<ITrackerRepository>())
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .BuildServiceProvider();

        var output = await new CurrentStatusDataset().RunAsync(
            new ReportDatasetRequest(
                new CurrentStatusParameters(), ReportPanelKind.StatTiles, Now.AddHours(-24), Now, UseMmol: false, scopes,
                services, new ReportReadLog(), Now: Now),
            CancellationToken.None);
        return (output, reader);
    }
}
