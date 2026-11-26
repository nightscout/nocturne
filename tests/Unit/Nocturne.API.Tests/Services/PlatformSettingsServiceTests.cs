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

    [Fact]
    public void WhatsApp_takes_an_optional_alert_template_name_and_language()
    {
        PlatformSettingsService.GetSchemas()["whatsapp"]
            .Where(f => f.Name is "alertTemplateName" or "alertTemplateLanguage")
            .Should().HaveCount(2).And.OnlyContain(f => !f.Required);
    }

    [Theory]
    [InlineData("nocturne_alert", "en", true)]
    [InlineData("glucose_alert_2", "en_US", true)]
    [InlineData("Nocturne Alert", "en", false)]
    [InlineData("nocturne_alert", "English", false)]
    [InlineData("nocturne_alert", "en-US", false)]
    public void An_alert_template_must_be_named_as_WhatsApp_Manager_names_it(string name, string language, bool valid)
    {
        var errors = PlatformSettingsService.ValidateFormats("whatsapp", new Dictionary<string, string>
        {
            ["alertTemplateName"] = name,
            ["alertTemplateLanguage"] = language,
        });

        errors.Should().HaveCount(valid ? 0 : 1);
    }
}
