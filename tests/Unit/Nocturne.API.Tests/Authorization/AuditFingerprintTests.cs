using Nocturne.API.Authorization;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Tests for <see cref="AuditFingerprint"/>, which is what the audit trail records in place of a
/// credential's stored digest.
/// </summary>
[Trait("Category", "Unit")]
public class AuditFingerprintTests
{
    private static readonly string Digest = HashUtils.Sha256Hex("a-stored-credential-digest");
    private static readonly string OtherDigest = HashUtils.Sha256Hex("another-stored-credential-digest");

    [Fact]
    public void Of_DifferentDigests_ProduceDifferentIdentities()
    {
        Assert.NotEqual(
            AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, Digest),
            AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, OtherDigest));
    }

    [Fact]
    public void Of_SameDigestAndDomain_IsStable()
    {
        Assert.Equal(
            AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, Digest),
            AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, Digest));
    }

    /// <summary>
    /// Without the domain constant, one credential family's fingerprint would be a lookup key in
    /// another's — a value read out of one audit table could be matched against the other.
    /// </summary>
    [Fact]
    public void Of_SameDigestUnderDifferentDomains_ProducesDifferentIdentities()
    {
        Assert.NotEqual(
            AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, Digest),
            AuditFingerprint.Of(AuditFingerprint.InstanceKeyDomain, Digest));
    }

    /// <summary>
    /// The digest authenticates the credential — presented on the wire for the instance key,
    /// matched by lookup for an API secret — so a fingerprint that leaked any part of it would hand
    /// a reader of the audit trail something usable.
    /// </summary>
    [Fact]
    public void Of_RevealsNoPartOfTheDigest()
    {
        var fingerprint = AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, Digest);

        Assert.NotNull(fingerprint);
        Assert.Equal(AuditFingerprint.Length, fingerprint!.Length);
        Assert.DoesNotContain(fingerprint, Digest);
        Assert.NotEqual(Digest[..AuditFingerprint.Length], fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Of_NoDigest_IsNull(string? digest)
    {
        Assert.Null(AuditFingerprint.Of(AuditFingerprint.ApiSecretDomain, digest));
    }
}
