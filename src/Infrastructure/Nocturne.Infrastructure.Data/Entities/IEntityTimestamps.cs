namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Entity has a CreatedAt timestamp set on insert
/// </summary>
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}

/// <summary>
/// Entity has an UpdatedAt timestamp set on insert and update
/// </summary>
public interface IHasUpdatedAt
{
    DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Entity has a SysCreatedAt timestamp set on insert
/// </summary>
public interface IHasSysCreatedAt
{
    DateTime SysCreatedAt { get; set; }
}

/// <summary>
/// Entity has a SysUpdatedAt timestamp set on insert and update
/// </summary>
public interface IHasSysUpdatedAt
{
    DateTime SysUpdatedAt { get; set; }
}
