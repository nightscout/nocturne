using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Models;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Compatibility;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

public class CorrelationServiceTests
{
    private readonly CorrelationService _service;
    private readonly ILogger<CorrelationService> _logger;

    public CorrelationServiceTests()
    {
        _logger = new LoggerFactory().CreateLogger<CorrelationService>();
        var config = Options.Create(
            new CompatibilityProxyConfiguration { EnableCorrelationTracking = true }
        );
        _service = new CorrelationService(config, TimeProvider.System, _logger);
    }

    [Fact]
    public void GenerateCorrelationId_TrackingEnabled_ShouldReturnValidId()
    {
        // Act
        var correlationId = _service.GenerateCorrelationId();

        // Assert
        Assert.NotEmpty(correlationId);
        Assert.StartsWith("INT-", correlationId);
        Assert.Contains("-", correlationId);
    }

    [Fact]
    public void GenerateCorrelationId_TrackingDisabled_ShouldReturnEmpty()
    {
        // Arrange
        var config = Options.Create(
            new CompatibilityProxyConfiguration { EnableCorrelationTracking = false }
        );
        var service = new CorrelationService(config, TimeProvider.System, _logger);

        // Act
        var correlationId = service.GenerateCorrelationId();

        // Assert
        Assert.Empty(correlationId);
    }

    [Fact]
    public void GenerateCorrelationId_StampsTheIdFromTheClock()
    {
        var config = Options.Create(
            new CompatibilityProxyConfiguration { EnableCorrelationTracking = true }
        );
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 3, 4, 5, TimeSpan.Zero));
        var service = new CorrelationService(config, clock, _logger);

        Assert.StartsWith("INT-20261029-030405-", service.GenerateCorrelationId());

        clock.Advance(TimeSpan.FromHours(21));
        Assert.StartsWith("INT-20261030-000405-", service.GenerateCorrelationId());
    }

    [Fact]
    public void GenerateCorrelationId_MultipleCalls_ShouldReturnUniqueIds()
    {
        // Act
        var id1 = _service.GenerateCorrelationId();
        var id2 = _service.GenerateCorrelationId();

        // Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GetCurrentCorrelationId_NotSet_ShouldReturnNull()
    {
        // Act
        var correlationId = _service.GetCurrentCorrelationId();

        // Assert
        Assert.Null(correlationId);
    }

    [Fact]
    public void SetAndGetCorrelationId_ValidId_ShouldStoreAndRetrieve()
    {
        // Arrange
        var testId = "test-correlation-id";

        // Act
        _service.SetCorrelationId(testId);
        var retrieved = _service.GetCurrentCorrelationId();

        // Assert
        Assert.Equal(testId, retrieved);
    }

    [Fact]
    public async Task CorrelationId_AsyncContext_ShouldBeIsolated()
    {
        // This test verifies that correlation IDs are properly isolated
        // between different async contexts using AsyncLocal

        // Arrange
        var tasks = new List<Task<string?>>();

        // Act
        for (int i = 0; i < 5; i++)
        {
            var taskId = i;
            tasks.Add(
                Task.Run(async () =>
                {
                    var correlationId = $"correlation-{taskId}";
                    _service.SetCorrelationId(correlationId);

                    // Simulate some async work
                    await Task.Delay(10);

                    return _service.GetCurrentCorrelationId();
                })
            );
        }

        await Task.WhenAll(tasks);

        // Assert
        for (int i = 0; i < tasks.Count; i++)
        {
            Assert.Equal($"correlation-{i}", await tasks[i]);
        }
    }
}
