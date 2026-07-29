using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Nocturne.Infrastructure.Data.Security;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for the at-rest wrapping of the TOTP secret column: the value converter round-trips,
/// the stored form is not the secret, and the purpose string is not shared with another flow.
/// </summary>
public class TotpSecretProtectionTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    [Fact]
    [Trait("Category", "Unit")]
    public void Converter_round_trips_a_secret()
    {
        var converter = TotpSecretProtection.CreateConverter(Provider.CreateProtector(TotpSecretProtection.Purpose));
        var secret = new byte[20];
        Random.Shared.NextBytes(secret);

        var stored = (byte[])converter.ConvertToProvider(secret)!;
        var read = (byte[])converter.ConvertFromProvider(stored)!;

        read.Should().Equal(secret);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Converter_does_not_store_the_secret_in_readable_form()
    {
        var converter = TotpSecretProtection.CreateConverter(Provider.CreateProtector(TotpSecretProtection.Purpose));
        var secret = Encoding.UTF8.GetBytes("12345678901234567890");

        var stored = (byte[])converter.ConvertToProvider(secret)!;

        stored.Should().NotEqual(secret);
        Encoding.UTF8.GetString(stored).Should().NotContain("12345678901234567890");
        TotpSecretProtection.IsProtectedPayload(stored).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Converter_rejects_a_plaintext_column_value()
    {
        var converter = TotpSecretProtection.CreateConverter(Provider.CreateProtector(TotpSecretProtection.Purpose));
        var plaintext = new byte[20];

        var read = () => converter.ConvertFromProvider(plaintext);

        // CryptographicException specifically: that is what TotpService.VerifyLoginAsync catches to
        // turn a lost key into an ordinary failed attempt instead of a 500.
        read.Should().Throw<CryptographicException>(
            "an unencrypted column value must fail closed rather than be read as a secret");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void A_payload_from_another_purpose_does_not_decrypt()
    {
        var stored = Provider.CreateProtector("Nocturne.Totp.Setup").Protect(new byte[20]);
        var converter = TotpSecretProtection.CreateConverter(Provider.CreateProtector(TotpSecretProtection.Purpose));

        var read = () => converter.ConvertFromProvider(stored);

        read.Should().Throw<CryptographicException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsProtectedPayload_is_false_for_a_bare_secret()
    {
        TotpSecretProtection.IsProtectedPayload(new byte[20]).Should().BeFalse();
        TotpSecretProtection.IsProtectedPayload([]).Should().BeFalse();
    }
}
