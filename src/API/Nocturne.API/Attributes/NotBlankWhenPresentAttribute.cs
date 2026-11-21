using System.ComponentModel.DataAnnotations;

namespace Nocturne.API.Attributes;

/// <summary>
/// A string that may be null, but never empty or whitespace. It is not <see cref="RequiredAttribute"/>,
/// which would mark the member required in the OpenAPI schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class NotBlankWhenPresentAttribute() : ValidationAttribute("The {0} field is required.")
{
    public override bool IsValid(object? value) => value is null || !string.IsNullOrWhiteSpace(value as string);
}
