using System.Linq;
using FluentAssertions;
using Nocturne.API.Services.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public sealed class ShareTokenGeneratorTests
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private readonly ShareTokenGenerator _generator = new();

    [Fact]
    public void Generate_returns_sixteen_characters()
    {
        // 16 Crockford-base32 characters = 80 bits. The token is stored only as an unsalted SHA-256
        // digest, so its entropy is the whole security argument against an offline search of a
        // leaked dump; 60 bits was within reach. It is only ever copy-pasted, never typed.
        _generator.Generate().Should().HaveLength(16);
    }

    [Fact]
    public void Generate_fits_a_dns_label()
    {
        // The token is the first label of {token}.share.{domain}, so it must stay inside the
        // 63-character limit.
        _generator.Generate().Length.Should().BeLessThanOrEqualTo(63);
    }

    [Fact]
    public void Generate_uses_only_the_lowercase_crockford_alphabet()
    {
        for (var i = 0; i < 200; i++)
            _generator.Generate().All(c => Alphabet.Contains(c)).Should().BeTrue();
    }

    [Fact]
    public void Generate_produces_distinct_tokens()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => _generator.Generate()).ToHashSet();
        tokens.Should().HaveCount(1000);
    }
}
