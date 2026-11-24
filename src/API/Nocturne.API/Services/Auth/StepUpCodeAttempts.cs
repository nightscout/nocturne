namespace Nocturne.API.Services.Auth;

/// <summary>What <see cref="StepUpCodeAttempts.Begin"/> allows for one authenticator code.</summary>
public enum StepUpAttemptVerdict
{
    /// <summary>Check the code.</summary>
    Allowed,

    /// <summary>Check the code; if it is wrong, the step-up token has no wrong codes left and is spent.</summary>
    LastForToken,

    /// <summary>The step-up token has no wrong codes left. Do not check the code.</summary>
    TokenSpent,

    /// <summary>The member has no wrong codes left until the window ends. Do not check the code.</summary>
    MemberLocked,
}

/// <summary>
/// Counts the authenticator codes tried at the sign-in step, per step-up token and per member. Only
/// someone who has completed the member's first factor reaches that step, so the counts bound what
/// such a person can guess however many client addresses they send from and however many step-up
/// tokens they mint.
/// </summary>
/// <remarks>
/// <para>
/// With one authenticator, 3 codes in 10^6 are valid at an instant (the previous, current and next
/// step), so an even chance takes about 231,000 guesses. <see cref="WrongCodesPerMember"/> per
/// <see cref="MemberWindow"/> allows 40 an hour, which puts an even chance at about 240 days of
/// continuous guessing. A member who mistypes gets <see cref="WrongCodesPerToken"/> tries per
/// sign-in and two sign-ins' worth before the wait.
/// </para>
/// <para>
/// Held in process. The API runs as one process (the Helm chart's "Single API process"), so one
/// count sees every attempt. A restart clears the counts; a token already spent stays spent, since
/// <see cref="TotpService"/> records that on its row.
/// </para>
/// <para>
/// Each attempt is charged before its code is checked, so concurrent requests cannot all pass the
/// check before any of them is counted. A correct code clears the member's count.
/// </para>
/// </remarks>
public sealed class StepUpCodeAttempts(TimeProvider time)
{
    /// <summary>Codes one step-up token takes before it is spent and the first factor is needed again.</summary>
    public const int WrongCodesPerToken = 5;

    /// <summary>Codes a member's step-up tokens take together before the member waits out <see cref="MemberWindow"/>.</summary>
    public const int WrongCodesPerMember = 10;

    /// <summary>Counted from the member's first wrong code; when it ends the count starts again.</summary>
    public static readonly TimeSpan MemberWindow = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Tally> _tokens = [];
    private readonly Dictionary<Guid, Tally> _members = [];

    private readonly record struct Tally(int Count, DateTimeOffset Until);

    /// <summary>
    /// Charges one code to <paramref name="tokenId"/> and <paramref name="subjectId"/>, unless either
    /// has none left.
    /// </summary>
    /// <param name="tokenId">The step-up token's row id. Its count is kept for
    /// <see cref="TotpService.StepUpExpiry"/> from its first code, which outlasts the token.</param>
    /// <param name="subjectId">The member the token stands for.</param>
    /// <returns>The verdict, and for <see cref="StepUpAttemptVerdict.MemberLocked"/> how long the member waits.</returns>
    public (StepUpAttemptVerdict Verdict, TimeSpan RetryAfter) Begin(
        Guid tokenId, Guid subjectId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);

            _members.TryGetValue(subjectId, out var member);
            if (member.Count >= WrongCodesPerMember)
                return (StepUpAttemptVerdict.MemberLocked, member.Until - now);

            _tokens.TryGetValue(tokenId, out var token);
            if (token.Count >= WrongCodesPerToken)
                return (StepUpAttemptVerdict.TokenSpent, TimeSpan.Zero);

            _members[subjectId] = new Tally(member.Count + 1, member.Count == 0 ? now + MemberWindow : member.Until);
            _tokens[tokenId] = new Tally(token.Count + 1, token.Count == 0 ? now + TotpService.StepUpExpiry : token.Until);

            return token.Count + 1 >= WrongCodesPerToken
                ? (StepUpAttemptVerdict.LastForToken, TimeSpan.Zero)
                : (StepUpAttemptVerdict.Allowed, TimeSpan.Zero);
        }
    }

    /// <summary>A correct code redeemed <paramref name="tokenId"/>: the member's count starts again.</summary>
    public void Succeeded(Guid tokenId, Guid subjectId)
    {
        lock (_gate)
        {
            _tokens.Remove(tokenId);
            _members.Remove(subjectId);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (id, tally) in _tokens)
        {
            if (tally.Until <= now)
                _tokens.Remove(id);
        }

        foreach (var (id, tally) in _members)
        {
            if (tally.Until <= now)
                _members.Remove(id);
        }
    }
}
