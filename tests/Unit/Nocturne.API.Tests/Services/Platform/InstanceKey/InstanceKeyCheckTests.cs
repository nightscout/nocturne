using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Platform.InstanceKey;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Shared.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform.InstanceKey;

public sealed class InstanceKeyCheckTests : IDisposable
{
    private readonly SqliteTestDatabase _database = TestDbContextFactory.CreateSqlite();

    [Fact]
    public async Task TheFirstReadWritesTheCheckAndTheSameKeyMatchesIt()
    {
        var key = Encryption("the-instance-key");

        (await Check(key).ChangeAsync()).Should().BeNull();
        var written = await GenerationAsync();
        (await Check(Encryption("the-instance-key")).ChangeAsync()).Should().BeNull();

        written.Should().NotBeNull();
        (await GenerationAsync()).Should().Be(written);
    }

    [Fact]
    public async Task ADifferentKeyRewritesTheCheckUnderANewGenerationThatStaysOutstanding()
    {
        await Check(Encryption("the-old-key")).ChangeAsync();
        var old = await GenerationAsync();

        var found = await Check(Encryption("the-new-key")).ChangeAsync();
        var again = await Check(Encryption("the-new-key")).ChangeAsync();

        found.Should().NotBeNull();
        found!.Value.Should().NotBe(old!.Value);
        (await GenerationAsync()).Should().Be(found);
        again.Should().Be(found, "a restart under the same new key resumes the change it found");
    }

    [Fact]
    public async Task AThirdKeyStartsAChangeOfItsOwn()
    {
        await Check(Encryption("key-a")).ChangeAsync();
        var second = await Check(Encryption("key-b")).ChangeAsync();

        var third = await Check(Encryption("key-c")).ChangeAsync();

        third.Should().NotBeNull();
        third!.Value.Should().NotBe(second!.Value);
    }

    [Fact]
    public async Task TwoKeysAlternatingDoNotTakeTheCheckFromEachOther()
    {
        var replaced = Check(Encryption("the-old-key"));
        var current = Check(Encryption("the-new-key"));
        await replaced.ChangeAsync();
        var change = (await current.ChangeAsync())!.Value;

        for (var pass = 0; pass < 3; pass++)
        {
            (await replaced.FindChangeAsync(CancellationToken.None))
                .Should().Be(new InstanceKeyFinding(InstanceKeyState.Superseded, change));
            (await current.ChangeAsync()).Should().Be(change);
        }

        (await GenerationAsync()).Should().Be(change);
    }

    [Fact]
    public async Task TheReplacedKeyReturningAfterTheCurrentOneStoppedIsAChangeOfItsOwn()
    {
        var replaced = Check(Encryption("the-old-key"));
        var current = Check(Encryption("the-new-key"));
        await replaced.ChangeAsync();
        var change = (await current.ChangeAsync())!.Value;
        await StopScanningAsync(InstanceKeyCheck.LivenessWindow + TimeSpan.FromMinutes(1));

        var rollback = await replaced.ChangeAsync();

        rollback.Should().NotBeNull();
        rollback!.Value.Should().NotBe(change);
        (await current.FindChangeAsync(CancellationToken.None))
            .Should().Be(new InstanceKeyFinding(InstanceKeyState.Superseded, rollback.Value),
                "the key that came back now scans, so the one it replaced defers to it");
    }

    [Fact]
    public async Task TheReplacedKeyDefersWhileTheCurrentOneScannedWithinTheWindow()
    {
        var replaced = Check(Encryption("the-old-key"));
        var current = Check(Encryption("the-new-key"));
        await replaced.ChangeAsync();
        var change = (await current.ChangeAsync())!.Value;
        await StopScanningAsync(InstanceKeyCheck.LivenessWindow - TimeSpan.FromMinutes(1));

        (await replaced.FindChangeAsync(CancellationToken.None)).State.Should().Be(InstanceKeyState.Superseded);
        (await GenerationAsync()).Should().Be(change);
    }

    [Fact]
    public async Task TheReplacedKeyReturningAfterTheChangeClearedIsAChangeOfItsOwn()
    {
        var replaced = Check(Encryption("the-old-key"));
        var current = Check(Encryption("the-new-key"));
        await replaced.ChangeAsync();
        var change = (await current.ChangeAsync())!.Value;
        await current.ClearAsync(change, CancellationToken.None);

        (await replaced.ChangeAsync()).Should().NotBeNull().And.NotBe(change);
    }

    [Fact]
    public async Task ClearingTheChangeQuietsTheNextStartAndAStaleClearDoesNothing()
    {
        var newKey = Encryption("the-new-key");
        await Check(Encryption("the-old-key")).ChangeAsync();
        var generation = (await Check(newKey).ChangeAsync())!.Value;

        await Check(newKey).ClearAsync(Guid.NewGuid(), CancellationToken.None);
        (await Check(newKey).ChangeAsync()).Should().Be(generation);

        await Check(newKey).ClearAsync(generation, CancellationToken.None);
        (await Check(newKey).ChangeAsync()).Should().BeNull();
        (await GenerationAsync()).Should().Be(generation);
    }

    [Fact]
    public async Task WithNoKeyNothingIsWrittenOrFound()
    {
        (await Check(Encryption(null)).ChangeAsync()).Should().BeNull();

        (await GenerationAsync()).Should().BeNull();
    }

    [Fact]
    public void OnlyCiphertextFromTheSameKeyDecrypts()
    {
        var key = Encryption("the-instance-key");
        var ciphertext = key.Encrypt("secret");

        key.CanDecrypt(ciphertext).Should().BeTrue();
        key.CanDecrypt("").Should().BeTrue();
        key.CanDecrypt(null).Should().BeTrue();
        Encryption("another-key").CanDecrypt(ciphertext).Should().BeFalse();
        key.CanDecrypt("not base64!").Should().BeFalse();
        key.CanDecrypt(Convert.ToBase64String(new byte[4])).Should().BeFalse();
        Encryption(null).CanDecrypt(ciphertext).Should().BeFalse();
    }

    /// <summary>Dates the current key's last scan <paramref name="ago"/>, as a replica that stopped would leave it.</summary>
    private async Task StopScanningAsync(TimeSpan ago)
    {
        await using var db = _database.CreateContext();
        var at = DateTime.UtcNow - ago;
        await db.InstanceKeyChecks.ExecuteUpdateAsync(u => u.SetProperty(c => c.CheckedAt, at));
    }

    private InstanceKeyCheck Check(ISecretEncryptionService key) =>
        new(_database.ContextFactory.AsNocturneFactory(), key);

    private async Task<Guid?> GenerationAsync()
    {
        await using var db = _database.CreateContext();
        return await db.InstanceKeyChecks.AsNoTracking().Select(c => (Guid?)c.Generation).SingleOrDefaultAsync();
    }

    private static ISecretEncryptionService Encryption(string? instanceKey) => new SecretEncryptionService(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ServiceNames.ConfigKeys.InstanceKey] = instanceKey })
            .Build(),
        NullLogger<SecretEncryptionService>.Instance);

    public void Dispose() => _database.Dispose();
}

internal static class InstanceKeyCheckTestExtensions
{
    /// <summary>The outstanding change this key acts on, or null.</summary>
    public static async Task<Guid?> ChangeAsync(this InstanceKeyCheck check) =>
        (await check.FindChangeAsync(CancellationToken.None)).Change;
}
