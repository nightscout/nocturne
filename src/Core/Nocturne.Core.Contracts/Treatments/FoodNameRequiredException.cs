namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// A <see cref="FoodWriteMode.Native"/> write left the food without a name.
/// </summary>
public sealed class FoodNameRequiredException : Exception
{
    public FoodNameRequiredException()
        : base("Name is required") { }
}
