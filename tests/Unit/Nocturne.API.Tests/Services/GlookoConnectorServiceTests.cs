using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Contracts;
using Xunit;

namespace Nocturne.API.Tests.Services
{
    public class GlookoConnectorServiceTests
    {
        private readonly Mock<IOptions<GlookoConnectorConfiguration>> _mockOptions;
        private readonly Mock<ILogger<GlookoConnectorService>> _mockLogger;
        private readonly Mock<ILogger<GlookoAuthTokenProvider>> _mockTokenLogger;
        private readonly Mock<IRetryDelayStrategy> _mockRetryDelay;
        private readonly Mock<IRateLimitingStrategy> _mockRateLimiting;
        private readonly Mock<ITreatmentClassificationService> _mockClassificationService;
        private readonly HttpClient _httpClient;

        public GlookoConnectorServiceTests()
        {
            _mockOptions = new Mock<IOptions<GlookoConnectorConfiguration>>();
            _mockLogger = new Mock<ILogger<GlookoConnectorService>>();
            _mockTokenLogger = new Mock<ILogger<GlookoAuthTokenProvider>>();
            _mockRetryDelay = new Mock<IRetryDelayStrategy>();
            _mockRateLimiting = new Mock<IRateLimitingStrategy>();
            _mockClassificationService = new Mock<ITreatmentClassificationService>();
            _httpClient = new HttpClient();
        }

        private GlookoConnectorService CreateService(GlookoConnectorConfiguration config)
        {
            _mockOptions.Setup(o => o.Value).Returns(config);
            var tokenProvider = new GlookoAuthTokenProvider(
                _mockOptions.Object,
                _httpClient,
                _mockTokenLogger.Object
            );

            return new GlookoConnectorService(
                _httpClient,
                _mockOptions.Object,
                _mockLogger.Object,
                _mockRetryDelay.Object,
                _mockRateLimiting.Object,
                tokenProvider,
                _mockClassificationService.Object
            );
        }

        [Fact]
        public void TransformBatchDataToTreatments_WithPositiveOffset_CurrentlyAddsOffset_ButShouldSubtract()
        {
            // Arrange
            // Test Case: Local Time 17:30. Offset +11 (Sydney).
            // Expectation: 17:30 Local - 11h = 06:30 UTC.

            // Current "Bug" Logic: 17:30 + 11h = 28:30 -> 04:30 Next Day.
            // This test verifies that we fix it to SUBTRACT.

            var config = new GlookoConnectorConfiguration
            {
                TimezoneOffset = 11 // Sydney +11
            };

            var service = CreateService(config);

            var batchData = new GlookoBatchData
            {
                // Food at 17:30 Local
                Foods = new[]
                {
                    new GlookoFood
                    {
                        Timestamp = "2025-12-16T17:30:00",
                        Carbs = 50
                    }
                },
                // Insulin at 17:30 Local
                Insulins = new[]
                {
                    new GlookoInsulin
                    {
                        Timestamp = "2025-12-16T17:30:00",
                        Value = 5.0
                    }
                }
            };

            // Act
            var treatments = service.TransformBatchDataToTreatments(batchData);

            // Assert
            var mealBolus = treatments.FirstOrDefault(t => t.EventType == "Meal Bolus");
            mealBolus.Should().NotBeNull();

            // Expected UTC Time: 17:30 - 11h = 06:30
            // We expect the resulting string to be in UTC format ends with Z

            // If the bug exists (ADD offset), it would be "2025-12-17T04:30:00.000Z"
            // We want "2025-12-16T06:30:00.000Z"

            var expectedUtc = "2025-12-16T06:30:00.000Z";

            mealBolus.EventTime.Should().Be(expectedUtc, "Positive timezone offset should be SUBTRACTED to get UTC");
        }
    }
}
