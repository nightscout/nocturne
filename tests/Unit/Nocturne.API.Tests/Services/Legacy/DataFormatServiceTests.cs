using System.Text;
using FluentAssertions;
using Microsoft.Net.Http.Headers;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Legacy;

/// <summary>
/// Comprehensive unit tests for DataFormatService covering all formatting scenarios
/// </summary>
public class DataFormatServiceTests
{

    #region GetContentType Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void GetContentType_WithCsvFormat_ShouldReturnTextCsv()
    {
        // Arrange
        var format = "csv";

        // Act
        var result = DataFormatService.GetContentType(format);

        // Assert
        result.Should().Be("text/csv");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void GetContentType_WithTsvFormat_ShouldReturnTextTabSeparatedValues()
    {
        // Arrange
        var format = "tsv";

        // Act
        var result = DataFormatService.GetContentType(format);

        // Assert
        result.Should().Be("text/tab-separated-values");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void GetContentType_WithTxtFormat_ShouldReturnTextPlain()
    {
        // Arrange
        var format = "txt";

        // Act
        var result = DataFormatService.GetContentType(format);

        // Assert
        result.Should().Be("text/plain");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void GetContentType_WithUpperCaseFormat_ShouldReturnCorrectType()
    {
        // Arrange
        var format = "CSV";

        // Act
        var result = DataFormatService.GetContentType(format);

        // Assert
        result.Should().Be("text/csv");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void GetContentType_WithUnsupportedFormat_ShouldReturnApplicationJson()
    {
        // Arrange
        var format = "xml";

        // Act
        var result = DataFormatService.GetContentType(format);

        // Assert
        result.Should().Be("application/json");
    }

    #endregion

    #region FormatEntries Error Handling Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithUnsupportedFormat_ShouldThrowArgumentException()
    {
        // Arrange
        var entries = new Entry[] { CreateSampleEntry() };
        var format = "xml";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            DataFormatService.FormatEntries(entries, format)
        );
        exception.Message.Should().Contain("Unsupported format: xml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithNullFormat_ShouldThrowArgumentNullException()
    {
        // Arrange
        var entries = new Entry[] { CreateSampleEntry() };
        string format = null!;

        // Act & Assert
        Assert.Throws<NullReferenceException>(() =>
            DataFormatService.FormatEntries(entries, format)
        );
    }

    #endregion

    #region FormatEntries Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithEmptyArray_ShouldReturnEmptyBody()
    {
        DataFormatService.FormatEntries([], "csv").Should().BeEmpty();
        DataFormatService.FormatEntries([], "tsv").Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsCsv_ShouldEmitFiveQuotedColumnsWithNoHeader()
    {
        var result = DataFormatService.FormatEntries([CreateSampleEntry()], "csv");

        result.Should().Be("\"2023-01-01T12:00:00.000Z\",1234567890,115,\"Flat\",\"TestDevice\"");
    }

    [Theory]
    [InlineData("tsv")]
    [InlineData("txt")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTabSeparatedText_ShouldUseTabsAndNoHeader(string format)
    {
        var result = DataFormatService.FormatEntries([CreateSampleEntry()], format);

        result.Should().Be("\"2023-01-01T12:00:00.000Z\"\t1234567890\t115\t\"Flat\"\t\"TestDevice\"");
    }

    [Theory]
    [InlineData("csv", ",")]
    [InlineData("tsv", "\t")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_ShouldJoinRowsWithCrlfAndNoTrailingNewline(string format, string separator)
    {
        var entries = new[]
        {
            new Entry { Mills = 2, Sgv = 120, DateString = "b" },
            new Entry { Mills = 1, Sgv = 110, DateString = "a" },
        };

        var result = DataFormatService.FormatEntries(entries, format);

        result
            .Should()
            .Be(
                $"\"b\"{separator}2{separator}120{separator}{separator}\r\n\"a\"{separator}1{separator}110{separator}{separator}"
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithNullFields_ShouldLeaveThemEmpty()
    {
        var entry = new Entry { Mills = 1234567890, Mgdl = 120, Type = "sgv" };

        var result = DataFormatService.FormatEntries([entry], "csv");

        result.Should().Be("\"1970-01-15T06:56:07.890Z\",1234567890,,,");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_ShouldJsonEscapeQuotesAndControlCharacters()
    {
        var entry = new Entry
        {
            Mills = 1,
            DateString = "d",
            Device = "device\"with\"quotes\twith\r\nbreaks",
        };

        var result = DataFormatService.FormatEntries([entry], "tsv");

        result.Should().Be("\"d\"\t1\t\t\t\"device\\\"with\\\"quotes\\twith\\r\\nbreaks\"");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithUnicodeCharacters_ShouldPreserveEncoding()
    {
        var entry = new Entry
        {
            Mills = 1234567890,
            Device = "デバイス",
            DateString = "2023-01-01T12:00:00.000Z",
        };

        var result = DataFormatService.FormatEntries([entry], "csv");

        result.Should().Contain("\"デバイス\"");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_ShouldEmitEveryRowOfALargeDataset()
    {
        var entries = Enumerable
            .Range(0, 1000)
            .Select(i => new Entry { Mills = 1234567890 + i, DateString = "d" })
            .ToArray();

        var result = DataFormatService.FormatEntries(entries, "csv");

        result.Split("\r\n").Should().HaveCount(1000);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithVeryLongStrings_ShouldKeepThemWhole()
    {
        var longString = new string('A', 10000);
        var entry = new Entry { Mills = 1, DateString = "d", Device = longString };

        var result = DataFormatService.FormatEntries([entry], "csv");

        result.Should().Contain($"\"{longString}\"");
    }

    #endregion

    #region NegotiateFormat Tests

    [Theory]
    [InlineData(null, "", null)]
    [InlineData("", "application/json", null)]
    [InlineData("json", "text/csv", "csv")]
    [InlineData("csv", "application/json", "csv")]
    [InlineData("xml", "", null)]
    [InlineData("xml", "text/csv", "csv")]
    [InlineData("TSV", "application/json", "tsv")]
    [InlineData(null, "text/csv;q=0", null)]
    [InlineData(null, "application/xml, text/csv;q=0", null)]
    [InlineData(null, "text/csv;q=0, text/plain", "txt")]
    [InlineData(null, "text/*", "txt")]
    [InlineData(null, "text/*, application/json", null)]
    [InlineData(null, "application/json, text/*", null)]
    [InlineData(null, "text/*, text/csv", "csv")]
    [InlineData(null, "text/*;q=0.1, application/json", null)]
    [InlineData(null, "application/xml", null)]
    [InlineData(null, "*/*", null)]
    [InlineData(null, "text/plain", "txt")]
    [InlineData(null, "text/plain, application/json", "txt")]
    [InlineData(null, "application/json, text/plain", null)]
    [InlineData(null, "application/json, text/csv;q=0.1", null)]
    [InlineData(null, "text/csv;q=0.1, application/json", null)]
    [InlineData(null, "text/csv, application/json;q=0.5", "csv")]
    [InlineData(null, "application/json, text/csv", null)]
    [InlineData(null, "text/csv, text/tab-separated-values;q=0.9", "csv")]
    [InlineData(null, "text/tab-separated-values;q=0.2, text/csv;q=0.8", "csv")]
    [InlineData(null, "text/html, */*;q=0.8", null)]
    [InlineData(null, "text/csv", "csv")]
    [InlineData(null, "TEXT/TAB-SEPARATED-VALUES", "tsv")]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void NegotiateFormat_ShouldPickTheTextFormatTheRequestAsksFor(
        string? format,
        string accept,
        string? expected
    )
    {
        var parsed = MediaTypeHeaderValue.ParseList(accept.Length == 0 ? [] : [accept]);

        DataFormatService.NegotiateFormat(format, parsed).Should().Be(expected);
    }

    #endregion

    #region FormatTreatments Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_WithUnsupportedFormat_ShouldThrowArgumentException()
    {
        // Arrange
        var treatments = new Treatment[] { CreateSampleTreatment() };
        var format = "xml";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            DataFormatService.FormatTreatments(treatments, format)
        );
        exception.Message.Should().Contain("Unsupported format: xml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_AsCsv_ShouldFormatCorrectly()
    {
        // Arrange
        var treatment = CreateSampleTreatment();
        var treatments = new Treatment[] { treatment };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id,timestamp,created_at,eventType,insulin,carbs,glucose,glucoseType,notes,enteredBy"
            );
        lines[1].Should().Contain("treatment-id");
        lines[1].Should().Contain("Meal Bolus");
        lines[1].Should().Contain("5");
        lines[1].Should().Contain("30");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_AsTsv_ShouldUseTabDelimiters()
    {
        // Arrange
        var treatment = CreateSampleTreatment();
        var treatments = new Treatment[] { treatment };
        var format = "tsv";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id\ttimestamp\tcreated_at\teventType\tinsulin\tcarbs\tglucose\tglucoseType\tnotes\tenteredBy"
            );
        lines[1].Should().Contain("\t");
        lines[1].Should().NotContain(",");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_AsTxt_ShouldCreateReadableFormat()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-id",
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 30.0,
            Created_at = "2023-01-01T12:00:00.000Z",
        };
        var treatments = new Treatment[] { treatment };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        result
            .Should()
            .Contain(
                "Treatment treatment-id: 5U insulin, 30g carbs at 2023-01-01T12:00:00.000Z (Meal Bolus)"
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_AsTxt_WithOnlyInsulin_ShouldShowOnlyInsulin()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-id",
            EventType = "Correction Bolus",
            Insulin = 2.5,
            Carbs = null,
            Created_at = "2023-01-01T12:00:00.000Z",
        };
        var treatments = new Treatment[] { treatment };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        result
            .Should()
            .Contain(
                "Treatment treatment-id: 2.5U insulin at 2023-01-01T12:00:00.000Z (Correction Bolus)"
            );
        result.Should().NotContain("carbs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_AsTxt_WithOnlyCarbs_ShouldShowOnlyCarbs()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-id",
            EventType = "Carb Correction",
            Insulin = null,
            Carbs = 15.0,
            Created_at = "2023-01-01T12:00:00.000Z",
        };
        var treatments = new Treatment[] { treatment };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        result
            .Should()
            .Contain(
                "Treatment treatment-id: 15g carbs at 2023-01-01T12:00:00.000Z (Carb Correction)"
            );
        result.Should().NotContain("insulin");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatTreatments_WithSpecialCharacters_ShouldEscapeProperly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment,with,commas",
            EventType = "Event\"with\"quotes",
            Insulin = 5.0,
            Carbs = 30.0,
            Notes = "Notes\nwith\nnewlines",
            EnteredBy = "User\twith\ttabs",
            Created_at = "2023-01-01T12:00:00.000Z",
        };
        var treatments = new Treatment[] { treatment };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        result.Should().Contain("\"treatment,with,commas\"");
        result.Should().Contain("\"Event\"\"with\"\"quotes\"");
    }

    #endregion

    #region FormatDeviceStatus Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_WithUnsupportedFormat_ShouldThrowArgumentException()
    {
        // Arrange
        var deviceStatuses = new DeviceStatus[] { CreateSampleDeviceStatus() };
        var format = "xml";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            DataFormatService.FormatDeviceStatus(deviceStatuses, format)
        );
        exception.Message.Should().Contain("Unsupported format: xml");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_AsCsv_ShouldFormatCorrectly()
    {
        // Arrange
        var deviceStatus = CreateSampleDeviceStatus();
        var deviceStatuses = new DeviceStatus[] { deviceStatus };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatDeviceStatus(deviceStatuses, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id,mills,created_at,device,uploader_battery,pump_battery_percent,iob_timestamp,iob_bolusiob,iob_basaliob"
            );
        lines[1].Should().Contain("device-status-id");
        lines[1].Should().Contain("Test Device");
        lines[1].Should().Contain("85");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_AsTsv_ShouldUseTabDelimiters()
    {
        // Arrange
        var deviceStatus = CreateSampleDeviceStatus();
        var deviceStatuses = new DeviceStatus[] { deviceStatus };
        var format = "tsv";

        // Act
        var result = DataFormatService.FormatDeviceStatus(deviceStatuses, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id\tmills\tcreated_at\tdevice\tuploader_battery\tpump_battery_percent\tiob_timestamp\tiob_bolusiob\tiob_basaliob"
            );
        lines[1].Should().Contain("\t");
        lines[1].Should().NotContain(",");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_AsTxt_ShouldCreateReadableFormat()
    {
        // Arrange
        var deviceStatus = new DeviceStatus
        {
            Id = "device-status-id",
            Device = "Test Device",
            CreatedAt = "2023-01-01T12:00:00.000Z",
            Uploader = new UploaderStatus { Battery = 85 },
        };
        var deviceStatuses = new DeviceStatus[] { deviceStatus };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatDeviceStatus(deviceStatuses, format);

        // Assert
        result
            .Should()
            .Contain(
                "Device Status device-status-id: Test Device at 2023-01-01T12:00:00.000Z (Battery: 85%)"
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_AsTxt_WithNullBattery_ShouldShowUnknown()
    {
        // Arrange
        var deviceStatus = new DeviceStatus
        {
            Id = "device-status-id",
            Device = "Test Device",
            CreatedAt = "2023-01-01T12:00:00.000Z",
            Uploader = null,
        };
        var deviceStatuses = new DeviceStatus[] { deviceStatus };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatDeviceStatus(deviceStatuses, format);

        // Assert
        result
            .Should()
            .Contain(
                "Device Status device-status-id: Test Device at 2023-01-01T12:00:00.000Z (Battery: unknown%)"
            );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatDeviceStatus_WithComplexPumpData_ShouldIncludeIobData()
    {
        // Arrange
        var deviceStatus = new DeviceStatus
        {
            Id = "device-status-id",
            Device = "Test Device",
            CreatedAt = "2023-01-01T12:00:00.000Z",
            Mills = 1234567890,
            Uploader = new UploaderStatus { Battery = 85 },
            Pump = new PumpStatus
            {
                Battery = new PumpBattery { Percent = 75 },
                Iob = new PumpIob
                {
                    Timestamp = "2023-01-01T12:00:00.000Z",
                    BolusIob = 2.5,
                    BasalIob = 1.2,
                },
            },
        };
        var deviceStatuses = new DeviceStatus[] { deviceStatus };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatDeviceStatus(deviceStatuses, format);

        // Assert
        result.Should().Contain("75"); // Pump battery percent
        result.Should().Contain("2.5"); // Bolus IOB
        result.Should().Contain("1.2"); // Basal IOB
        result.Should().Contain("2023-01-01T12:00:00.000Z"); // IOB timestamp
    }

    #endregion

    #region Large Dataset Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void FormatTreatments_WithLargeDataset_ShouldEmitEveryRow()
    {
        // Arrange
        var treatments = new Treatment[1000];
        for (int i = 0; i < 1000; i++)
        {
            treatments[i] = new Treatment
            {
                Id = $"treatment-{i}",
                EventType = "Meal Bolus",
                Insulin = 5.0 + (i % 10),
                Carbs = 30.0 + (i % 20),
                Created_at = "2023-01-01T12:00:00.000Z",
            };
        }
        var format = "csv";

        // Act
        var result = DataFormatService.FormatTreatments(treatments, format);

        // Assert
        result.Should().NotBeNullOrEmpty();
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(1001); // Header + 1000 data rows
    }

    #endregion

    #region Helper Methods

    private static Entry CreateSampleEntry()
    {
        return new Entry
        {
            Id = "test-id",
            Mills = 1234567890,
            Mgdl = 120,
            Sgv = 115,
            Type = "sgv",
            Device = "TestDevice",
            Direction = "Flat",
            DateString = "2023-01-01T12:00:00.000Z",
            Filtered = 110.5,
            Unfiltered = 112.3,
            Rssi = -50,
            Noise = 1,
        };
    }

    private static Treatment CreateSampleTreatment()
    {
        return new Treatment
        {
            Id = "treatment-id",
            EventType = "Meal Bolus",
            Insulin = 5.0,
            Carbs = 30.0,
            Glucose = 120.0,
            GlucoseType = "Finger",
            Notes = "Test treatment",
            EnteredBy = "TestUser",
            Created_at = "2023-01-01T12:00:00.000Z",
            Timestamp = "2023-01-01T12:00:00.000Z",
        };
    }

    private static DeviceStatus CreateSampleDeviceStatus()
    {
        return new DeviceStatus
        {
            Id = "device-status-id",
            Mills = 1234567890,
            CreatedAt = "2023-01-01T12:00:00.000Z",
            Device = "Test Device",
            Uploader = new UploaderStatus { Battery = 85 },
            Pump = new PumpStatus
            {
                Battery = new PumpBattery { Percent = 75 },
                Iob = new PumpIob
                {
                    Timestamp = "2023-01-01T12:00:00.000Z",
                    BolusIob = 2.5,
                    BasalIob = 1.2,
                },
            },
        };
    }

    #endregion
}
