using FluentValidation.TestHelper;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Validators.Auth;
using Xunit;

namespace Nocturne.API.Tests.Validators.Auth;

public class PasskeyRegisterOptionsRequestValidatorTests
{
    private readonly PasskeyRegisterOptionsRequestValidator _validator = new();

    private static PasskeyRegisterOptionsRequest ValidRequest() => new()
    {
        Username = "testuser",
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
    public void Empty_username_fails(string? username)
    {
        var request = ValidRequest();
        request.Username = username!;
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }

    [Fact]
    public void Username_exceeding_max_length_fails()
    {
        var request = ValidRequest();
        request.Username = new string('a', 201);
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }
}
