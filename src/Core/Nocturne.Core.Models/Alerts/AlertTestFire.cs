namespace Nocturne.Core.Models.Alerts;

/// <summary>What a test fire of a saved rule left behind and did not send.</summary>
/// <param name="InstanceId">The test instance its delivery rows hang from.</param>
/// <param name="NotSent">The channels it did not send because they do not deliver.</param>
public sealed record AlertTestFire(Guid InstanceId, IReadOnlyList<AlertChannelNotSent> NotSent);
