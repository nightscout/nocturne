using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Covers the NUL stripping in <see cref="NocturneDbContext"/>'s SaveChanges walk. The store is
/// in-memory SQLite, which accepts a NUL happily: every assertion is on the persisted value, never
/// on the absence of an exception.
/// </summary>
[Trait("Category", "Unit")]
public class NulSanitizationTests : IDisposable
{
    // Spelled as constants so this source file carries no NUL character of its own.
    private const string NulEscape = @"\u0000";
    private const string EscapedBackslash = @"\\";
    private const char Nul = '\0';

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly PropertyStateRecorder _recorder = new();
    private readonly SqliteTestDatabase _db;

    public NulSanitizationTests()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns((HttpContext)null!);

        _db = TestDbContextFactory.CreateSqliteWithTenant(
            _tenantId, "test", _recorder, new MutationAuditInterceptor(accessor.Object));
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AddedRow_LosesTheNulInATextColumn()
    {
        await using (var ctx = _db.CreateContext())
        {
            ctx.MeterGlucose.Add(NewReading(device: "xDrip" + Nul));
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var reading = await verify.MeterGlucose.SingleAsync();
        reading.Device.Should().Be("xDrip");
    }

    [Fact]
    public async Task ModifiedRow_LosesTheNulInATextColumn()
    {
        var id = await SeedReadingAsync(device: "meter");

        await using (var ctx = _db.CreateContext())
        {
            var reading = await ctx.MeterGlucose.SingleAsync(r => r.Id == id);
            reading.Device = "iPhone" + Nul;
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var persisted = await verify.MeterGlucose.SingleAsync();
        persisted.Device.Should().Be("iPhone");
    }

    [Fact]
    public async Task TextColumn_KeepsTheEscapeThatOnlyJsonbRejects()
    {
        await using (var ctx = _db.CreateContext())
        {
            ctx.MeterGlucose.Add(NewReading(device: "xDrip" + NulEscape));
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var reading = await verify.MeterGlucose.SingleAsync();
        reading.Device.Should().Be("xDrip" + NulEscape,
            "in a text column those are six ordinary characters Postgres stores as they are");
    }

    [Theory]
    [InlineData(NulEscape, "")]
    [InlineData(EscapedBackslash + NulEscape, EscapedBackslash)]
    [InlineData(EscapedBackslash + EscapedBackslash + NulEscape, EscapedBackslash + EscapedBackslash)]
    [InlineData(EscapedBackslash + "u0000", EscapedBackslash + "u0000")]
    [InlineData(EscapedBackslash + EscapedBackslash + "u0000", EscapedBackslash + EscapedBackslash + "u0000")]
    public async Task JsonbColumn_LosesTheNulEscape_AndKeepsTheBackslashRunInFrontOfIt(
        string fragment, string expected)
    {
        await using (var ctx = _db.CreateContext())
        {
            var reading = NewReading(device: "xDrip");
            reading.AdditionalPropertiesJson = $$"""{"a":"x{{fragment}}y"}""";
            ctx.MeterGlucose.Add(reading);
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var persisted = await verify.MeterGlucose.SingleAsync();
        persisted.AdditionalPropertiesJson.Should().Be($$"""{"a":"x{{expected}}y"}""");
    }

    [Fact]
    public async Task ModifiedRow_LeavesTheColumnsWithNoNulUnmodified()
    {
        var id = await SeedReadingAsync(device: "meter", app: "uploader");

        await using var ctx = _db.CreateContext();
        var reading = await ctx.MeterGlucose.SingleAsync(r => r.Id == id);
        reading.Device = "iPhone" + Nul;
        await ctx.SaveChangesAsync();

        _recorder.Properties[nameof(MeterGlucoseEntity.Device)].CurrentValue.Should().Be("iPhone");
        _recorder.Properties[nameof(MeterGlucoseEntity.App)].IsModified.Should().BeFalse(
            "a string with no NUL is left alone rather than rewritten");
    }

    [Fact]
    public async Task AuditSnapshot_RecordsTheSanitizedValue()
    {
        var id = await SeedReadingAsync(device: "meter");

        await using (var ctx = _db.CreateContext())
        {
            ctx.AuditContext = new UserAuditContext();
            var reading = await ctx.MeterGlucose.SingleAsync(r => r.Id == id);
            reading.Device = "iPhone" + Nul;
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var log = await verify.MutationAuditLog.SingleAsync(l => l.Action == "update");
        log.ChangesJson.Should().Contain("iPhone");
        log.ChangesJson.Should().NotContain(NulEscape,
            "the walk runs before the interceptor, so the diff holds the persisted value "
                + "(a NUL would serialize into the audit row's own jsonb column as this escape)");
    }

    private MeterGlucoseEntity NewReading(string device, string? app = null) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = _tenantId,
        Timestamp = new DateTime(2016, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        Mgdl = 120,
        Device = device,
        App = app,
    };

    private async Task<Guid> SeedReadingAsync(string device, string? app = null)
    {
        await using var ctx = _db.CreateContext();
        var reading = NewReading(device, app);
        ctx.MeterGlucose.Add(reading);
        await ctx.SaveChangesAsync();
        return reading.Id;
    }

    /// <summary>
    /// Captures the change-tracking state of the row under test at the point every SaveChanges
    /// interceptor sees it: after the DbContext walk, before the statement is generated.
    /// </summary>
    private sealed class PropertyStateRecorder : SaveChangesInterceptor
    {
        public Dictionary<string, (object? CurrentValue, bool IsModified)> Properties { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<MeterGlucoseEntity>())
            {
                foreach (var property in entry.Properties)
                {
                    Properties[property.Metadata.Name] = (property.CurrentValue, property.IsModified);
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "PUT /api/v4/meter-glucose";
        public bool IsSystem => false;
    }
}
