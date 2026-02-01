using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/treatments endpoints.
/// V3 API follows a generic CRUD pattern with:
/// - SEARCH: GET /treatments
/// - CREATE: POST /treatments
/// - READ: GET /treatments/{id}
/// - UPDATE: PUT /treatments/{id}
/// - DELETE: DELETE /treatments/{id}
/// - BULK: POST /treatments/bulk
/// </summary>
public class TreatmentsParityTests : ParityTestBase
{
    public TreatmentsParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region SEARCH - GET /api/v3/treatments

    [Fact]
    public async Task Search_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/treatments");
    }

    [Fact]
    public async Task Search_WithData_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 2.0),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus", insulin: 4.0),
            TestDataFactory.CreateTreatment(eventType: "Carb Correction")
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments");
    }

    [Fact]
    public async Task Search_WithLimit_ReturnsSameShape()
    {
        var treatments = Enumerable.Range(0, 10)
            .Select(i => TestDataFactory.CreateTreatment(
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-i * 30),
                insulin: 1.0 + i * 0.5))
            .ToArray();
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?limit=5");
    }

    [Fact]
    public async Task Search_WithSkip_ReturnsSameShape()
    {
        var treatments = Enumerable.Range(0, 10)
            .Select(i => TestDataFactory.CreateTreatment(
                timestamp: TestTimeProvider.GetTestTime().AddMinutes(-i * 30)))
            .ToArray();
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?skip=3");
    }

    [Fact]
    public async Task Search_WithSort_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(timestamp: TestTimeProvider.GetTestTime()),
            TestDataFactory.CreateTreatment(timestamp: TestTimeProvider.GetTestTime().AddHours(-2)),
            TestDataFactory.CreateTreatment(timestamp: TestTimeProvider.GetTestTime().AddHours(-1))
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?sort=created_at");
        await AssertGetParityAsync("/api/v3/treatments?sort$desc=created_at");
    }

    [Fact]
    public async Task Search_WithFields_ReturnsSameShape()
    {
        var treatment = TestDataFactory.CreateTreatment(eventType: "Correction Bolus", insulin: 2.0);
        await SeedTreatmentsAsync(treatment);

        await AssertGetParityAsync("/api/v3/treatments?fields=eventType,insulin,created_at");
    }

    #region Filter Operators

    [Fact]
    public async Task Search_Filter_EventType_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus")
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?eventType$eq=Correction%20Bolus");
    }

    [Fact]
    public async Task Search_Filter_Insulin_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(insulin: 1.0),
            TestDataFactory.CreateTreatment(insulin: 3.0),
            TestDataFactory.CreateTreatment(insulin: 5.0)
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?insulin$gte=2");
        await AssertGetParityAsync("/api/v3/treatments?insulin$lte=3");
    }

    [Fact]
    public async Task Search_Filter_In_ReturnsSameShape()
    {
        var treatments = new[]
        {
            TestDataFactory.CreateTreatment(eventType: "Correction Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Meal Bolus"),
            TestDataFactory.CreateTreatment(eventType: "Temp Basal")
        };
        await SeedTreatmentsAsync(treatments);

        await AssertGetParityAsync("/api/v3/treatments?eventType$in=Correction%20Bolus|Meal%20Bolus");
    }

    #endregion

    #endregion

    #region CREATE - POST /api/v3/treatments

    [Fact]
    public async Task Create_Bolus_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Correction Bolus",
            insulin = 2.5,
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_MealBolus_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Meal Bolus",
            insulin = 4.0,
            carbs = 45,
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test",
            notes = "Lunch"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_TempBasal_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Temp Basal",
            duration = 30,
            percent = -50,
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_ProfileSwitch_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Profile Switch",
            profile = "Day",
            duration = 0,
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_SiteChange_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Site Change",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test",
            notes = "New infusion site"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_SensorStart_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Sensor Start",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_Note_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Note",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            enteredBy = "Test",
            notes = "Feeling well today"
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    #endregion

    #region BULK CREATE - POST /api/v3/treatments/bulk

    [Fact]
    public async Task BulkCreate_ReturnsSameShape()
    {
        var treatments = new object[]
        {
            new
            {
                eventType = "Correction Bolus",
                insulin = 1.5,
                created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            },
            new
            {
                eventType = "Meal Bolus",
                insulin = 3.0,
                carbs = 30,
                created_at = TestTimeProvider.GetTestTime().AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            },
            new
            {
                eventType = "Temp Basal",
                duration = 60,
                percent = 150,
                created_at = TestTimeProvider.GetTestTime().AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            }
        };

        await AssertPostParityAsync("/api/v3/treatments/bulk", treatments);
    }

    [Fact]
    public async Task BulkCreate_Empty_ReturnsSameShape()
    {
        var treatments = Array.Empty<object>();

        await AssertPostParityAsync("/api/v3/treatments/bulk", treatments);
    }

    #endregion

    #region READ - GET /api/v3/treatments/{id}

    [Fact]
    public async Task Read_NotFound_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/treatments/nonexistent123456789012");
    }

    #endregion

    #region UPDATE - PUT /api/v3/treatments/{id}

    [Fact]
    public async Task Update_NotFound_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "Correction Bolus",
            insulin = 3.0,
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        };

        await AssertPutParityAsync("/api/v3/treatments/nonexistent123456789012", treatment);
    }

    #endregion

    #region DELETE - DELETE /api/v3/treatments/{id}

    [Fact]
    public async Task Delete_NotFound_ReturnsSameShape()
    {
        await AssertDeleteParityAsync("/api/v3/treatments/nonexistent123456789012");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task Create_Empty_ReturnsSameShape()
    {
        var treatment = new { };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Create_InvalidEventType_ReturnsSameShape()
    {
        var treatment = new
        {
            eventType = "",
            created_at = TestTimeProvider.GetTestTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        };

        await AssertPostParityAsync("/api/v3/treatments", treatment);
    }

    [Fact]
    public async Task Search_InvalidLimit_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/treatments?limit=-1");
    }

    #endregion
}
