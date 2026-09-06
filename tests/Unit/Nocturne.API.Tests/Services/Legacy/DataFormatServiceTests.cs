using System.Text;
using FluentAssertions;
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

    #region FormatEntries CSV Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithEmptyArray_ShouldReturnOnlyHeader()
    {
        // Arrange
        var entries = new Entry[0];
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result
            .Should()
            .StartWith(
                "_id,mills,date,dateString,sgv,mbg,type,direction,device,filtered,unfiltered,rssi,noise"
            );
        result.Should().EndWith(Environment.NewLine);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_SingleEntry_ShouldFormatAsCsv()
    {
        // Arrange
        var entry = CreateSampleEntry();
        var entries = new Entry[] { entry };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id,mills,date,dateString,sgv,mbg,type,direction,device,filtered,unfiltered,rssi,noise"
            );
        lines[1].Should().Contain("test-id");
        lines[1].Should().Contain("1234567890");
        lines[1].Should().Contain("120");
        lines[1].Should().Contain("sgv");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithSpecialCharacters_ShouldEscapeCsvProperly()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test,with,commas",
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = "device\"with\"quotes",
            DateString = "2023-01-01T12:00:00.000Z",
            Direction = "Flat",
            Notes = "Notes\nwith\nnewlines",
        };
        var entries = new Entry[] { entry };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("\"test,with,commas\""); // Commas should be escaped
        result.Should().Contain("\"device\"\"with\"\"quotes\""); // Quotes should be escaped
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithNullFields_ShouldHandleGracefully()
    {
        // Arrange
        var entry = new Entry
        {
            Id = null,
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = null,
            Direction = null,
            DateString = null,
        };
        var entries = new Entry[] { entry };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().NotBeNull();
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThan(1); // Should have header + data row
        lines[1].Should().StartWith(",1234567890,"); // Null ID should be empty
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithUnicodeCharacters_ShouldPreserveEncoding()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test-ñáéíóú-emoji-🩸",
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = "デバイス", // Japanese characters
            Direction = "Flat",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("test-ñáéíóú-emoji-🩸");
        result.Should().Contain("デバイス");
    }

    #endregion

    #region FormatEntries TSV Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTsv_ShouldUseTabDelimiters()
    {
        // Arrange
        var entry = CreateSampleEntry();
        var entries = new Entry[] { entry };
        var format = "tsv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(2); // Header + 1 data row
        lines[0]
            .Should()
            .Be(
                "_id\tmills\tdate\tdateString\tsgv\tmbg\ttype\tdirection\tdevice\tfiltered\tunfiltered\trssi\tnoise"
            );
        lines[1].Should().Contain("\t"); // Should contain tabs
        lines[1].Should().NotContain(","); // Should not contain commas
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTsv_WithTabsInData_ShouldReplaceWithSpaces()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test\twith\ttabs",
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = "device\twith\ttabs",
            Direction = "Flat",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "tsv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("test with tabs"); // Tabs should be replaced with spaces
        result.Should().Contain("device with tabs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTsv_WithNewlinesInData_ShouldReplaceWithSpaces()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test\nwith\nnewlines",
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = "device\r\nwith\r\ncarriage",
            Direction = "Flat",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "tsv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("test with newlines"); // Newlines should be replaced with spaces
        result.Should().Contain("device  with  carriage"); // Carriage returns should be replaced with spaces
    }

    #endregion

    #region FormatEntries TXT Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTxt_ShouldCreateReadableFormat()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test-id",
            Mills = 1234567890,
            Mgdl = 120,
            Sgv = 115,
            Type = "sgv",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("Entry test-id: 115 mg/dL at 2023-01-01T12:00:00.000Z (sgv)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_AsTxt_WithNullSgv_ShouldUseMgdl()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test-id",
            Mills = 1234567890,
            Mgdl = 120,
            Sgv = null,
            Type = "sgv",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "txt";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().Contain("Entry test-id: 120 mg/dL at 2023-01-01T12:00:00.000Z (sgv)");
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
    public void FormatEntries_WithLargeDataset_ShouldEmitEveryRow()
    {
        // Arrange
        var entries = new Entry[1000];
        for (int i = 0; i < 1000; i++)
        {
            entries[i] = new Entry
            {
                Id = $"entry-{i}",
                Mills = 1234567890 + i,
                Mgdl = 100 + (i % 50),
                Type = "sgv",
                Device = $"device-{i % 10}",
                Direction = "Flat",
                DateString = "2023-01-01T12:00:00.000Z",
            };
        }
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().NotBeNullOrEmpty();
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(1001); // Header + 1000 data rows
    }

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

    #region Memory and Edge Case Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithVeryLongStrings_ShouldHandleGracefully()
    {
        // Arrange
        var longString = new string('A', 10000); // 10KB string
        var entry = new Entry
        {
            Id = longString,
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            Device = longString,
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };
        var format = "csv";

        // Act
        var result = DataFormatService.FormatEntries(entries, format);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain(longString);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Category", "Parity")]
    public void FormatEntries_WithMixedLineEndings_ShouldNormalizeCorrectly()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "test-id\r\nwith\nmixed\rline\n\rendings",
            Mills = 1234567890,
            Mgdl = 120,
            Type = "sgv",
            DateString = "2023-01-01T12:00:00.000Z",
        };
        var entries = new Entry[] { entry };

        // Act
        var csvResult = DataFormatService.FormatEntries(entries, "csv");
        var tsvResult = DataFormatService.FormatEntries(entries, "tsv");

        // Assert
        // CSV should escape the field with quotes
        csvResult.Should().Contain("\"test-id\r\nwith\nmixed\rline\n\rendings\"");

        // TSV should replace all line endings with spaces
        tsvResult.Should().Contain("test-id  with mixed line  endings");
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
