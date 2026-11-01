namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A row of a type deduplication links. <see cref="IsDuplicate"/> projects <c>linked_records</c> onto
/// the row: true exactly when the row's link is not its group's primary. Only the deduplication
/// module writes it, by statement, from the links (<see cref="Extensions.DuplicateGroupPrimaries"/>;
/// <c>DuplicateFlagWriterGuardTests</c> holds the list); the model never saves it, so no tracked
/// insert or update of the row can reset it.
/// </summary>
public interface IDuplicateFlagged : IIdentified
{
    bool IsDuplicate { get; set; }
}
