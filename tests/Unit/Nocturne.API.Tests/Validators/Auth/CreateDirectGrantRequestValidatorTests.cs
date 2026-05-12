using FluentValidation.TestHelper;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Validators.Auth;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Validators.Auth;

public class CreateDirectGrantRequestValidatorTests
{
    private readonly CreateDirectGrantRequestValidator _validator = new();

    private static CreateDirectGrantRequest ValidRequest() => new()
    {
        Label = "My API Token",
        Scopes = [OAuthScopes.GlucoseRead],
    };

    [Fact]
    public void Valid_request_passes()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_label_fails(string? label)
    {
        var request = ValidRequest();
        request.Label = label!;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Label);
    }

    [Fact]
    public void Label_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Label = new string('a', 201);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Label);
    }

    [Fact]
    public void Empty_scopes_fails()
    {
        var request = ValidRequest();
        request.Scopes = [];
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Scopes);
    }

    [Fact]
    public void Null_scopes_fails()
    {
        var request = ValidRequest();
        request.Scopes = null!;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Scopes);
    }

    [Fact]
    public void Invalid_scope_fails()
    {
        var request = ValidRequest();
        request.Scopes = ["not.a.real.scope"];
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor("Scopes[0]");
    }

    [Fact]
    public void Valid_scopes_pass()
    {
        var request = ValidRequest();
        request.Scopes = [OAuthScopes.GlucoseRead, OAuthScopes.TreatmentsReadWrite, OAuthScopes.FullAccess];
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
