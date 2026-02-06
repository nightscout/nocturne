namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Entity has a CreatedAt timestamp set on insert
/// </summary>
public interface IHasCreatedAt
{
    /// <inheritdoc cref="IHasCreatedAt"/>
    DateTime CreatedAt { get; set; }
}

/// <summary>
/// Entity has an UpdatedAt timestamp set on insert and update
/// </summary>
public interface IHasUpdatedAt
{
    /// <inheritdoc cref="IHasUpdatedAt"/>
    DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Entity has a SysCreatedAt timestamp set on insert
/// </summary>
public interface IHasSysCreatedAt
{
    /// <inheritdoc cref="IHasSysCreatedAt"/>
    DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// Entity has a SysUpdatedAt timestamp set on insert and update
/// </summary>
public interface IHasSysUpdatedAt
{
    /// <inheritdoc cref="IHasSysUpdatedAt"/>
    DateTime SysUpdatedAt { get; set; }
}
