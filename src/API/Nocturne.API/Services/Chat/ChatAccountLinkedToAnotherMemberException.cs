namespace Nocturne.API.Services.Chat;

/// <summary>
/// A chat account holds one link per tenant, and this tenant's link for it belongs to another
/// member, so claiming it for the caller would leave the bot acting as that member.
/// </summary>
/// <seealso cref="ChatIdentityDirectoryService.CreateLinkAsync"/>
public sealed class ChatAccountLinkedToAnotherMemberException()
    : Exception("This chat account is already linked to another member of this tenant");
