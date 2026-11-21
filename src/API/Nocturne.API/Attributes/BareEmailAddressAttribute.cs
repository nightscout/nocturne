using System.ComponentModel.DataAnnotations;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Attributes;

/// <summary>
/// A string that is, once trimmed, one bare email address (<see cref="EmailAddresses.IsBareAddress"/>).
/// A null value is left to <see cref="RequiredAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BareEmailAddressAttribute() : ValidationAttribute("The {0} field must be one email address.")
{
    public override bool IsValid(object? value) => value is null || EmailAddresses.IsBareAddress(value as string);
}
