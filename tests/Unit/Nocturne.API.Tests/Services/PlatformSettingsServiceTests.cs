using FluentAssertions;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

public class PlatformSettingsServiceTests
{
    [Fact]
    public void A_telegram_bot_cannot_be_enabled_without_its_webhook_secret_token()
    {
        PlatformSettingsService.GetSchemas()["telegram"]
            .Should().ContainSingle(f => f.Name == "webhookSecretToken")
            .Which.Required.Should().BeTrue();
    }

    private static Dictionary<string, string> TelegramFormatErrors(string token) =>
        PlatformSettingsService.ValidateFormats("telegram", new Dictionary<string, string>
        {
            ["webhookSecretToken"] = token,
        });

    [Theory]
    [InlineData("a")]
    [InlineData("Abc_123-xyz")]
    public void A_webhook_secret_token_in_telegrams_alphabet_is_accepted(string token)
    {
        TelegramFormatErrors(token).Should().BeEmpty();
        TelegramFormatErrors(new string('a', 256)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("ünicode")]
    public void A_webhook_secret_token_outside_telegrams_alphabet_is_refused(string token)
    {
        TelegramFormatErrors(token).Should().ContainKey("webhookSecretToken");
    }

    [Fact]
    public void A_webhook_secret_token_longer_than_256_characters_is_refused()
    {
        TelegramFormatErrors(new string('a', 257)).Should().ContainKey("webhookSecretToken");
    }

    [Fact]
    public void A_blank_webhook_secret_token_keeps_the_stored_one_rather_than_failing_its_format()
    {
        TelegramFormatErrors("").Should().BeEmpty();
    }
}
