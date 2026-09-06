using FluentAssertions;
using Moq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests;

/// <summary>
///     SaveSecretsAsync replaces the whole secrets document, so connectors that rotate a token at
///     runtime must merge. These cover that the merge keeps what it is not asked to change.
/// </summary>
public class ConnectorSecretExtensionsTests
{
    private static (Mock<IConnectorConfigurationService> Service, Dictionary<string, string> Saved) Storage(
        Dictionary<string, string> existing)
    {
        var saved = new Dictionary<string, string>();
        var service = new Mock<IConnectorConfigurationService>();

        service
            .Setup(s => s.GetSecretsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        service
            .Setup(s => s.SaveSecretsAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, string>, string?, CancellationToken>(
                (_, secrets, _, _) =>
                {
                    saved.Clear();
                    foreach (var (k, v) in secrets) saved[k] = v;
                })
            .Returns(Task.CompletedTask);

        return (service, saved);
    }

    [Fact]
    public async Task MergeSecretsAsync_KeepsSecretsItWasNotAskedToChange()
    {
        var (service, saved) = Storage(new Dictionary<string, string>
        {
            ["password"] = "user-password",
            ["refresh_token"] = "old-token",
        });

        var changed = await service.Object.MergeSecretsAsync(
            "CareLink", new Dictionary<string, string?> { ["refresh_token"] = "new-token" });

        changed.Should().BeTrue();
        saved.Should().Contain("refresh_token", "new-token");
        saved.Should().Contain("password", "user-password");
    }

    [Fact]
    public async Task MergeSecretsAsync_RemovesKeysWithAClearedValue()
    {
        var (service, saved) = Storage(new Dictionary<string, string>
        {
            ["password"] = "user-password",
            ["pageCursor"] = "page-7",
        });

        var changed = await service.Object.MergeSecretsAsync(
            "MyFitnessPal", new Dictionary<string, string?> { ["pageCursor"] = null });

        changed.Should().BeTrue();
        saved.Should().NotContainKey("pageCursor");
        saved.Should().Contain("password", "user-password");
    }

    [Fact]
    public async Task MergeSecretsAsync_DoesNotSave_WhenNothingChanged()
    {
        var (service, _) = Storage(new Dictionary<string, string> { ["refresh_token"] = "same" });

        var changed = await service.Object.MergeSecretsAsync(
            "CareLink",
            new Dictionary<string, string?> { ["refresh_token"] = "same", ["absent"] = null });

        changed.Should().BeFalse();
        service.Verify(
            s => s.SaveSecretsAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MergeSecretsAsync_RefusesToSave_WhenAStoredSecretCouldNotBeDecrypted()
    {
        // Decryption substitutes an empty string but keeps the key, so saving would re-encrypt a
        // blank over ciphertext that may only be transiently unreadable.
        var (service, _) = Storage(new Dictionary<string, string>
        {
            ["password"] = string.Empty,
            ["refresh_token"] = "old-token",
        });

        var changed = await service.Object.MergeSecretsAsync(
            "CareLink", new Dictionary<string, string?> { ["refresh_token"] = "new-token" });

        changed.Should().BeFalse();
        service.Verify(
            s => s.SaveSecretsAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MergeSecretsAsync_AddsNewKeys()
    {
        var (service, saved) = Storage(new Dictionary<string, string> { ["password"] = "pw" });

        await service.Object.MergeSecretsAsync(
            "MyFitnessPal",
            new Dictionary<string, string?> { ["userId"] = "123", ["syncCursor"] = "cursor" });

        saved.Should().Contain("userId", "123");
        saved.Should().Contain("syncCursor", "cursor");
        saved.Should().Contain("password", "pw");
    }
}
