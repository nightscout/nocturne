namespace Nocturne.API.Models.DevOnly;

/// <summary>
/// A committed, Development-only fixture of developer identities and their real
/// WebAuthn credentials. WebAuthn public keys are not secret and subjects are
/// global (not tenant-scoped), so the fixture can be committed and re-inserted
/// after a database wipe — the developer's real authenticator then signs in
/// without re-registering. Exported by GET api/v4/dev-only/auth/passkey-fixture,
/// consumed at API startup (Development only) and by seed-tenant, which adds
/// every fixture subject as an owner of the seeded tenant.
/// </summary>
public class DevIdentityFixtureFile
{
    public List<DevIdentityDto> Identities { get; set; } = [];
}

/// <summary>
/// One developer identity: a global subject plus its registered passkeys.
/// The subject id must stay fixed — it is the WebAuthn user handle recorded
/// inside the authenticator at registration time.
/// </summary>
public class DevIdentityDto
{
    public Guid SubjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Email { get; set; }
    public List<DevIdentityCredentialDto> Credentials { get; set; } = [];
}

/// <summary>
/// A stored WebAuthn credential (public key only). SignCount is intentionally
/// absent: credentials are always re-inserted with sign_count = 0, which any
/// real authenticator's next assertion counter exceeds.
/// </summary>
public class DevIdentityCredentialDto
{
    public string CredentialId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public List<string> Transports { get; set; } = [];
    public string? Label { get; set; }
    public Guid? AaGuid { get; set; }
}
