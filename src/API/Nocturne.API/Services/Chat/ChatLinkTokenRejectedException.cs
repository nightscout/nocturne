namespace Nocturne.API.Services.Chat;

/// <summary>
/// A pending link token that cannot be claimed here: unknown, expired, already used, or issued for
/// another tenant. The chat user has to start again with a fresh token.
/// </summary>
/// <seealso cref="ChatIdentityService.ClaimPendingLinkAsync"/>
public sealed class ChatLinkTokenRejectedException(string message) : Exception(message);
