namespace Nocturne.API.Services.Identity;

/// <summary>
/// A refusal thrown out of the work of <c>DbUnitOfWork.CommitAsync</c>, so its transaction rolls
/// back what the work had already saved: a work that returns normally commits. A refusal after
/// an earlier write in the same work, such as the removal of an ended membership or the write of
/// a grant, must leave what that write changed as it was.
/// </summary>
internal sealed class RefusedCommitException<TRefusal>(TRefusal refusal) : Exception
{
    public TRefusal Refusal { get; } = refusal;
}
