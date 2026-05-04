using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Unit tests for AlexaService
/// Tests maintain 1:1 compatibility with legacy Alexa implementation
/// </summary>
[Parity("api.alexa.test.js")]
public class AlexaServiceTests
{
    private readonly Mock<IEntryStore> _mockEntryStore;
    private readonly Mock<ILogger<AlexaService>> _mockLogger;
    private readonly AlexaService _alexaService;

    public AlexaServiceTests()
    {
        _mockEntryStore = new Mock<IEntryStore>();
        _mockLogger = new Mock<ILogger<AlexaService>>();
        _alexaService = new AlexaService(_mockEntryStore.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ProcessRequestAsync_LaunchRequest_ReturnsSpeechletResponse()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails { Type = "LaunchRequest", Locale = "en-US" },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.False(result.Response.ShouldEndSession);
        Assert.Contains("Hello", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_SessionEndedRequest_ReturnsEmptyResponse()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails { Type = "SessionEndedRequest" },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Null(result.Response.OutputSpeech);
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_NSStatus_ReturnsStatusResponse()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "NSStatus" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("Nightscout system", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_NSBg_WithData_ReturnsBloodSugarReading()
    {
        // Arrange
        var mockEntry = new Entry
        {
            Sgv = 120,
            Mills = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
        };

        _mockEntryStore
            .Setup(x => x.QueryAsync(It.Is<EntryQuery>(q => q.Type == "sgv" && q.Count == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mockEntry });

        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "NSBg" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("120", result.Response.OutputSpeech?.Text ?? "");
        Assert.Contains("minutes ago", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_NSBg_NoData_ReturnsNoDataMessage()
    {
        // Arrange
        _mockEntryStore
            .Setup(x => x.QueryAsync(It.Is<EntryQuery>(q => q.Type == "sgv" && q.Count == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entry>());

        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "NSBg" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("couldn't find any recent", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_HelpIntent_ReturnsHelpMessage()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "AMAZON.HelpIntent" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.False(result.Response.ShouldEndSession);
        Assert.Contains("I can help you check", result.Response.OutputSpeech?.Text ?? "");
        Assert.NotNull(result.Response.Reprompt);
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_StopIntent_ReturnsGoodbyeMessage()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "AMAZON.StopIntent" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("Goodbye", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_IntentRequest_UnknownIntent_ReturnsUnknownMessage()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "IntentRequest",
                Locale = "en-US",
                Intent = new AlexaIntent { Name = "UnknownIntent" },
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Contains("I didn't understand", result.Response.OutputSpeech?.Text ?? "");
    }

    [Fact]
    public async Task ProcessRequestAsync_LocaleProcessing_ExtractsCorrectLocale()
    {
        // Arrange
        var request = new AlexaRequest
        {
            Request = new AlexaRequestDetails
            {
                Type = "LaunchRequest",
                Locale = "es-ES", // Should be reduced to "es"
            },
        };

        // Act
        var result = await _alexaService.ProcessRequestAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        // The response should use Spanish translations if available
        // For this test, we're just ensuring it doesn't crash with non-English locales
        Assert.NotNull(result.Response.OutputSpeech?.Text);
    }

    [Fact]
    public void BuildSpeechletResponse_WithAllParameters_CreatesValidResponse()
    {
        // Arrange
        var title = "Test Title";
        var output = "Test output speech";
        var reprompt = "Test reprompt";
        var shouldEndSession = false;

        // Act
        var result = _alexaService.BuildSpeechletResponse(
            title,
            output,
            reprompt,
            shouldEndSession
        );

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0", result.Version);
        Assert.NotNull(result.Response);
        Assert.Equal(shouldEndSession, result.Response.ShouldEndSession);
        Assert.Equal(output, result.Response.OutputSpeech?.Text);
        Assert.Equal(title, result.Response.Card?.Title);
        Assert.Equal(output, result.Response.Card?.Content);
        Assert.NotNull(result.Response.Reprompt);
        Assert.Equal(reprompt, result.Response.Reprompt.OutputSpeech?.Text);
    }

    [Fact]
    public void BuildSpeechletResponse_WithEndSession_NoReprompt()
    {
        // Arrange
        var title = "Test Title";
        var output = "Test output speech";
        var reprompt = "Test reprompt";
        var shouldEndSession = true;

        // Act
        var result = _alexaService.BuildSpeechletResponse(
            title,
            output,
            reprompt,
            shouldEndSession
        );

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Response.ShouldEndSession);
        Assert.Null(result.Response.Reprompt);
    }
}
