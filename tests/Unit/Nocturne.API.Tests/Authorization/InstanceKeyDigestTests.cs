using Microsoft.Extensions.Configuration;
using Nocturne.API.Authorization;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Tests for <see cref="InstanceKeyDigest"/>, which gives an instance-key caller — a caller with no
/// subject of its own — an identity in the audit trail.
/// </summary>
[Trait("Category", "Unit")]
public class InstanceKeyDigestTests
{
    private const string FirstKey = "first-instance-key";
    private const string SecondKey = "second-instance-key";

    [Fact]
    public void ResolveFingerprint_DifferentKeys_ProduceDifferentIdentities()
    {
        var first = InstanceKeyDigest.ResolveFingerprint(ConfigFor(FirstKey));
        var second = InstanceKeyDigest.ResolveFingerprint(ConfigFor(SecondKey));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveFingerprint_SameKey_IsStable()
    {
        Assert.Equal(
            InstanceKeyDigest.ResolveFingerprint(ConfigFor(FirstKey)),
            InstanceKeyDigest.ResolveFingerprint(ConfigFor(FirstKey)));
    }

    /// <summary>
    /// The digest is what a caller presents in the <c>X-Instance-Key</c> header, so a fingerprint
    /// that leaked any part of it would hand a reader of the audit trail a usable credential.
    /// </summary>
    [Fact]
    public void ResolveFingerprint_RevealsNeitherTheKeyNorItsHeaderDigest()
    {
        var digest = HashUtils.Sha256Hex(FirstKey);
        var fingerprint = InstanceKeyDigest.ResolveFingerprint(ConfigFor(FirstKey));

        Assert.NotNull(fingerprint);
        Assert.NotEqual(digest, fingerprint);
        Assert.DoesNotContain(fingerprint, digest);
        Assert.DoesNotContain(FirstKey, fingerprint);
    }

    [Fact]
    public void ResolveFingerprint_NoKeyConfigured_IsNull()
    {
        Assert.Null(InstanceKeyDigest.ResolveFingerprint(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void RequireKey_AKeyOfTheMinimumLength_IsReturned()
    {
        var key = new string('k', InstanceKeyDigest.MinimumLength);

        Assert.Equal(key, InstanceKeyDigest.RequireKey(ConfigFor(key)));
    }

    [Fact]
    public void RequireKey_AShorterKey_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => InstanceKeyDigest.RequireKey(ConfigFor(new string('k', InstanceKeyDigest.MinimumLength - 1))));

        Assert.Contains("at least 32", ex.Message);
    }

    [Fact]
    public void RequireKey_NoKeyConfigured_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstanceKeyDigest.RequireKey(new ConfigurationBuilder().Build()));
    }

    private static IConfiguration ConfigFor(string instanceKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = instanceKey,
            })
            .Build();
}
