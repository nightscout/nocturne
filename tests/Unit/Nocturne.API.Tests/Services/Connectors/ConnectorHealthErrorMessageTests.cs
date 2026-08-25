using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The health-state write fits the joined sync errors to the column; see
/// <see cref="ConnectorConfigurationEntity.LastErrorMessageMaxLength"/>.
/// </summary>
public class ConnectorHealthErrorMessageTests : IDisposable
{
    private const string ConnectorName = "Glooko";
    private const string TruncationMarker = "... (truncated)";

    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly ConnectorConfigurationService _service;

    public ConnectorHealthErrorMessageTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(dbOptions) { TenantId = _tenantId };
        _dbContext.Database.EnsureCreated();

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
            IsActive = true,
        });
        _dbContext.ConnectorConfigurations.Add(new ConnectorConfigurationEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            ConnectorName = ConnectorName,
            IsHealthy = true,
        });
        _dbContext.SaveChanges();

        var encryptionService = new Mock<ISecretEncryptionService>();
        encryptionService.Setup(e => e.IsConfigured).Returns(true);

        _service = new ConnectorConfigurationService(
            _dbContext,
            encryptionService.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            NullLogger<ConnectorConfigurationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpdateHealthStateAsync_WithAnOverlongErrorMessage_StoresItTruncatedAndMarked()
    {
        // Enough distinct chunk errors that de-duplication cannot bring the join under the column.
        var errors = Enumerable.Range(1, 40)
            .Select(i => $"Chunk {i}/40 failed (2026-01-{i % 28 + 1:00} to 2026-02-{i % 28 + 1:00})");
        var joined = string.Join("; ", errors);
        joined.Length.Should().BeGreaterThan(ConnectorConfigurationEntity.LastErrorMessageMaxLength);

        await _service.UpdateHealthStateAsync(ConnectorName, lastErrorMessage: joined, isHealthy: false);

        var stored = (await _service.GetHealthStateAsync(ConnectorName))!.LastErrorMessage!;
        stored.Length.Should().Be(ConnectorConfigurationEntity.LastErrorMessageMaxLength);
        stored.Should().EndWith(TruncationMarker);
        stored.Should().StartWith(joined[..100]);
    }

    /// <summary>
    /// A cut that lands between a surrogate pair leaves a lone surrogate, which the driver's strict
    /// UTF-8 encoder rejects — the same failed write the truncation exists to prevent.
    /// </summary>
    [Fact]
    public async Task UpdateHealthStateAsync_WhenTheCutLandsInsideASurrogatePair_StoresEncodableText()
    {
        // The cut keeps 985 chars less the marker, so the pair's high surrogate lands on the boundary.
        var message = new string('a', 984) + "😀" + new string('b', 100);
        message.Length.Should().BeGreaterThan(ConnectorConfigurationEntity.LastErrorMessageMaxLength);

        await _service.UpdateHealthStateAsync(ConnectorName, lastErrorMessage: message, isHealthy: false);

        // The tracked entity, not a re-read: SQLite substitutes a replacement character on the way
        // out, which would hide the lone surrogate the driver has to encode.
        var stored = _dbContext.ConnectorConfigurations.Local.Single().LastErrorMessage!;
        stored.Length.Should().BeLessThanOrEqualTo(ConnectorConfigurationEntity.LastErrorMessageMaxLength);
        stored.Should().EndWith(TruncationMarker);
        stored.Should().NotContainAny("\uD83D", "\uDE00");

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var encode = () => strictUtf8.GetBytes(stored);
        encode.Should().NotThrow<EncoderFallbackException>("a lone surrogate cannot be encoded for the write");
    }

    [Fact]
    public async Task UpdateHealthStateAsync_WithAMessageThatFits_StoresItUnchanged()
    {
        const string message = "StateSpans publish failed";

        await _service.UpdateHealthStateAsync(ConnectorName, lastErrorMessage: message, isHealthy: false);

        var stored = (await _service.GetHealthStateAsync(ConnectorName))!.LastErrorMessage;
        stored.Should().Be(message);
    }

    [Fact]
    public async Task UpdateHealthStateAsync_AtExactlyTheColumnLength_StoresItUnchanged()
    {
        var message = new string('x', ConnectorConfigurationEntity.LastErrorMessageMaxLength);

        await _service.UpdateHealthStateAsync(ConnectorName, lastErrorMessage: message, isHealthy: false);

        var stored = (await _service.GetHealthStateAsync(ConnectorName))!.LastErrorMessage;
        stored.Should().Be(message);
    }
}
