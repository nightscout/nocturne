using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/devicestatus endpoints.
/// V3 API follows a generic CRUD pattern with:
/// - SEARCH: GET /devicestatus
/// - CREATE: POST /devicestatus
/// - READ: GET /devicestatus/{id}
/// - UPDATE: PUT /devicestatus/{id}
/// - DELETE: DELETE /devicestatus/{id}
/// </summary>
public class DeviceStatusParityTests : ParityTestBase
{
    public DeviceStatusParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region SEARCH - GET /api/v3/devicestatus

    [Fact]
    public async Task Search_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/devicestatus");
    }

    [Fact]
    public async Task Search_WithData_ReturnsSameShape()
    {
        var statuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(device: "loop://iPhone"),
            TestDataFactory.CreateDeviceStatus(device: "openaps://rpi")
        };
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v3/devicestatus");
    }

    [Fact]
    public async Task Search_WithLimit_ReturnsSameShape()
    {
        var statuses = Enumerable.Range(0, 10)
            .Select(i => TestDataFactory.CreateDeviceStatus(
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-i * 5)))
            .ToArray();
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v3/devicestatus?limit=5");
    }

    [Fact]
    public async Task Search_WithSkip_ReturnsSameShape()
    {
        var statuses = Enumerable.Range(0, 10)
            .Select(i => TestDataFactory.CreateDeviceStatus(
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-i * 5)))
            .ToArray();
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v3/devicestatus?skip=3");
    }

    [Fact]
    public async Task Search_WithSort_ReturnsSameShape()
    {
        var statuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(timestamp: TestTimeProvider.GetTestTime()),
            TestDataFactory.CreateDeviceStatus(timestamp: TestTimeProvider.GetTestTime().AddMinutes(-10)),
            TestDataFactory.CreateDeviceStatus(timestamp: TestTimeProvider.GetTestTime().AddMinutes(-5))
        };
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v3/devicestatus?sort=created_at");
        await AssertGetParityAsync("/api/v3/devicestatus?sort$desc=created_at");
    }

    [Fact]
    public async Task Search_Filter_Device_ReturnsSameShape()
    {
        var statuses = new[]
        {
            TestDataFactory.CreateDeviceStatus(device: "loop://iPhone"),
            TestDataFactory.CreateDeviceStatus(device: "openaps://rpi"),
            TestDataFactory.CreateDeviceStatus(device: "loop://iPhone")
        };
        await SeedDeviceStatusAsync(statuses);

        await AssertGetParityAsync("/api/v3/devicestatus?device$eq=loop://iPhone");
    }

    #endregion

    #region CREATE - POST /api/v3/devicestatus

    [Fact]
    public async Task Create_Simple_ReturnsSameShape()
    {
        var status = new
        {
            device = "openaps://test-rig",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            uploaderBattery = 85
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    [Fact]
    public async Task Create_WithPump_ReturnsSameShape()
    {
        var status = new
        {
            device = "openaps://test-rig",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            pump = new
            {
                battery = new { percent = 75 },
                reservoir = 120.5,
                clock = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                status = new { status = "normal", suspended = false }
            }
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    [Fact]
    public async Task Create_WithOpenAPS_ReturnsSameShape()
    {
        var status = new
        {
            device = "openaps://myopenaps",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            openaps = new
            {
                suggested = new
                {
                    bg = 120,
                    eventualBG = 110,
                    reason = "COB: 0, Dev: -10, BGI: -2, ISF: 50"
                },
                enacted = new
                {
                    bg = 120,
                    eventualBG = 110,
                    reason = "COB: 0, Dev: -10, BGI: -2, ISF: 50",
                    rate = 0.5,
                    duration = 30
                },
                iob = new
                {
                    iob = 1.5,
                    basaliob = 0.3,
                    activity = 0.02
                }
            }
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    [Fact]
    public async Task Create_WithLoop_ReturnsSameShape()
    {
        var status = new
        {
            device = "loop://iPhone",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            loop = new
            {
                predicted = new
                {
                    startDate = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    values = new[] { 120, 118, 116, 114, 112, 110 }
                },
                enacted = new
                {
                    timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    rate = 0.75,
                    duration = 30,
                    received = true
                },
                iob = new
                {
                    timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    iob = 2.5
                },
                cob = new
                {
                    timestamp = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    cob = 15
                }
            }
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    [Fact]
    public async Task Create_WithUploader_ReturnsSameShape()
    {
        var status = new
        {
            device = "xdrip://phone",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            uploader = new
            {
                battery = 78
            }
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    #endregion

    #region READ - GET /api/v3/devicestatus/{id}

    [Fact]
    public async Task Read_NotFound_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/devicestatus/nonexistent123456789012");
    }

    #endregion

    #region UPDATE - PUT /api/v3/devicestatus/{id}

    [Fact]
    public async Task Update_NotFound_ReturnsSameShape()
    {
        var status = new
        {
            device = "openaps://test",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        };

        await AssertPutParityAsync("/api/v3/devicestatus/nonexistent123456789012", status);
    }

    #endregion

    #region DELETE - DELETE /api/v3/devicestatus/{id}

    [Fact]
    public async Task Delete_NotFound_ReturnsSameShape()
    {
        await AssertDeleteParityAsync("/api/v3/devicestatus/nonexistent123456789012");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task Create_Empty_ReturnsSameShape()
    {
        var status = new { };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    [Fact]
    public async Task Create_MissingDevice_ReturnsSameShape()
    {
        var status = new
        {
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            uploaderBattery = 50
        };

        await AssertPostParityAsync("/api/v3/devicestatus", status);
    }

    #endregion
}
