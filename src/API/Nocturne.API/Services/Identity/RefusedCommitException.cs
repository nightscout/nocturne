namespace Nocturne.API.Services.Identity;

/// <summary>
/// A refusal thrown out of the work of <c>DbUnitOfWork.CommitAsync</c>, so its transaction rolls
/// back what the work had already saved: a work that returns normally commits. Joining again
/// removes the ended membership before the new one is added, and a refusal after that must leave
/// the ended row, its grants and its chat links as they were.
/// </summary>
internal sealed class RefusedCommitException<TRefusal>(TRefusal refusal) : Exception
{
    public TRefusal Refusal { get; } = refusal;
}
