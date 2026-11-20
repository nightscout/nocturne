using System.Security.Cryptography;
using System.Text;

namespace Nocturne.Services.SelfHostSetup;

public enum ClaimResult
{
    Accepted,
    Rejected,
    LockedOut,
}

/// <summary>
/// Proves the person at the setup page owns the machine: the code is the cloud's instance ID,
/// shown in the owner's console but nowhere on the network. Some providers number instances
/// sequentially, so wrong guesses are capped for the life of the process and a reboot is the only
/// way to try again.
/// </summary>
public sealed class ClaimGuard
{
    public const int MaxFailures = 5;

    private readonly byte[] _code;
    private int _failures;

    public ClaimGuard(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        _code = Encoding.UTF8.GetBytes(code.Trim());
    }

    public ClaimResult Check(string? attempt)
    {
        if (Volatile.Read(ref _failures) >= MaxFailures)
            return ClaimResult.LockedOut;

        var candidate = Encoding.UTF8.GetBytes(attempt?.Trim() ?? "");
        if (CryptographicOperations.FixedTimeEquals(candidate, _code))
            return ClaimResult.Accepted;

        Interlocked.Increment(ref _failures);
        return ClaimResult.Rejected;
    }
}
