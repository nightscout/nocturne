/** Fallback shown when a submission fails for a reason we can't safely surface. */
export function genericSubmitError(): string {
  return "We could not save your changes. Try again.";
}

/** For a form whose follow-up step, after the record itself saved, threw. */
export function followUpError(): string {
  return "Your changes were saved, but the step after saving did not finish. Try again.";
}

/** Shown when a rate limiter turned the attempt away, so the credential is unspent. */
export function rateLimitedError(): string {
  return "Too many attempts. Wait a few minutes and try again.";
}

/** Shown when the thing an action referred to is no longer on the server. */
export function missingItemError(): string {
  return "That item no longer exists. Refresh the page to see what's there now.";
}
