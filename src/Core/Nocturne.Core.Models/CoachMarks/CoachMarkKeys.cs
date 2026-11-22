namespace Nocturne.Core.Models.CoachMarks;

/// <summary>
/// Keys of coach marks the backend reads. The web app writes them through the coach mark endpoints.
/// </summary>
public static class CoachMarkKeys
{
    /// <summary>The prompt to add a second way to sign in.</summary>
    public const string BackupSignInPrompt = "account.backup-sign-in";

    /// <summary>
    /// The reminder to add a recovery email. A dismissal lapses, so the backend reads when it
    /// was made (<see cref="CoachMarkState.CompletedAt"/>).
    /// </summary>
    public const string RecoveryEmailPrompt = "account.recovery-email";
}
