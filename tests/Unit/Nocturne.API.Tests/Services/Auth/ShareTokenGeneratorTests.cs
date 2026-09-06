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
        // 16 Crockford-base32 characters = 80 bits, and a DNS label allows up to 63.
        _generator.Generate().Should().HaveLength(16);
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
