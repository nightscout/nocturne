namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     A verification request that cannot be run as asked. Its message names no secret value.
/// </summary>
public sealed class InvalidVerificationRequestException(string message) : Exception(message);
