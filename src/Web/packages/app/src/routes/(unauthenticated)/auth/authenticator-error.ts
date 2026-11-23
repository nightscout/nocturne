import { errorStatus } from "#lib/forms/submit-error.js";

/**
 * Why an authenticator-code sign-in did not succeed.
 *
 * - `not-a-member` - both factors passed, but this address does not admit the account.
 * - `rate-limited` - a limit turned the attempt away before the API read the code, so a correct
 *   code is still unspent. Two limits answer 429: the per-address `totp-login` limiter, and the
 *   member lock that holds once the member's wrong codes are used up until a window ends.
 * - `rejected` - the code was refused. The API answers a wrong code, a spent step-up token and an
 *   expired one the same way, so these cannot be told apart.
 */
export type AuthenticatorFailure = "not-a-member" | "rate-limited" | "rejected";

export function classifyAuthenticatorError(err: unknown): AuthenticatorFailure {
  switch (errorStatus(err)) {
    case 403:
      return "not-a-member";
    case 429:
      return "rate-limited";
    default:
      return "rejected";
  }
}
