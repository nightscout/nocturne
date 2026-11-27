using FluentAssertions;
using FluentValidation.TestHelper;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.Validators.V4;

public class UpsertNoteRequestValidatorTests
{
    private readonly UpsertNoteRequestValidator _validator = new();

    private static UpsertNoteRequest ValidRequest() => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Text = "Test note",
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Default_timestamp_fails()
    {
        var request = ValidRequest();
        request.Timestamp = default;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Timestamp);
    }

    [Fact]
    public void Text_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Text = new string('a', 10001);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Text);
    }

    [Fact]
    public void EventType_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.EventType = new string('a', ColumnLengths.Note.EventType + 1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.EventType);
    }

    [Fact]
    public void Device_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Device = new string('a', RecordColumnLengths.Provenance + 1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Device);
    }
}
