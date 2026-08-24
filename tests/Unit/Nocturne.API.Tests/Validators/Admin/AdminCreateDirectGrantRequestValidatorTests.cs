using FluentValidation.TestHelper;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.API.Validators.Admin;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Validators.Admin;

public class AdminCreateDirectGrantRequestValidatorTests
{
    private readonly AdminCreateDirectGrantRequestValidator _validator = new();

    private static AdminCreateDirectGrantRequest ValidRequest() => new()
    {
        SubjectId = Guid.CreateVersion7(),
        Label = "Partner Integration",
        Scopes = [Scope.GlucoseRead],
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_subject_fails()
    {
        var request = ValidRequest();
        request.SubjectId = Guid.Empty;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.SubjectId);
    }

    [Fact]
    public void Empty_label_fails_via_shared_rules()
    {
        var request = ValidRequest();
        request.Label = "";
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Label);
    }

    [Fact]
    public void Future_expiry_passes()
    {
        var request = ValidRequest();
        request.ExpiresAt = DateTime.UtcNow.AddDays(1);
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Past_expiry_fails_via_shared_rules()
    {
        var request = ValidRequest();
        request.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ExpiresAt);
    }
}
