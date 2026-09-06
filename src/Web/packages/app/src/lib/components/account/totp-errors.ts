/**
 * Turns a refused authenticator setup into something a person can act on.
 *
 * The server names the check that refused — `TotpSetupFailure` — and never the
 * wording, so the copy lives here. Both `setup` and `verify-setup` carry the
 * value as the `detail` of their 400, which the generated remote wrapper
 * forwards as the thrown `HttpError`'s `body.message`.
 */

import { TotpSetupFailure } from "$api-clients";
import { describeSubmitError, errorMessage, errorStatus } from "$lib/forms/submit-error";

const RESTART_SETUP = "Start it again from your security settings.";

/** Exhaustive: a failure the server can raise must have copy here. */
const SETUP_FAILURES: Record<TotpSetupFailure, string> = {
  [TotpSetupFailure.InvalidCode]:
    "That code wasn't accepted. Check your authenticator app and try again.",
  [TotpSetupFailure.ChallengeUnreadable]: `This two-factor setup is no longer valid. ${RESTART_SETUP}`,
  [TotpSetupFailure.ChallengeExpired]: `This two-factor setup took too long. ${RESTART_SETUP}`,
  [TotpSetupFailure.NoPrimaryFactor]:
    "Add a passkey or link a sign-in provider first. An authenticator app is a second step, not a way in on its own.",
  [TotpSetupFailure.SubjectNotFound]:
    "We couldn't load your account. Sign out and back in, then try again.",
};

function setupFailure(err: unknown): TotpSetupFailure | undefined {
  const body = errorMessage(err)?.trim();
  // Own keys only: `in` would also answer to "toString" and "constructor",
  // which resolve to functions rather than copy.
  return body !== undefined && Object.hasOwn(SETUP_FAILURES, body)
    ? (body as TotpSetupFailure)
    : undefined;
}

/** Shown when completing setup fails for a reason we can't name. */
export const TOTP_SETUP_FALLBACK =
  "Verification failed. Check the code and try again.";

/** Shown when starting setup fails for a reason we can't name. */
export const TOTP_SETUP_START_FALLBACK =
  "We couldn't start authenticator setup. Please try again.";

/**
 * @param err The thrown value.
 * @param fallback Shown when nothing more specific applies.
 */
export function describeTotpSetupError(
  err: unknown,
  fallback = TOTP_SETUP_FALLBACK
): string {
  const failure = setupFailure(err);
  if (failure !== undefined) return SETUP_FAILURES[failure];

  // Every 400 from this endpoint is a failure code, so one this build doesn't
  // know is not copy — showing it verbatim, as describeSubmitError would, puts
  // an identifier in front of the user.
  if (errorStatus(err) === 400) return fallback;

  return describeSubmitError(err, fallback);
}

/**
 * The same map for the call that *starts* enrolment, which refuses for its own
 * reasons and so carries its own fallback.
 *
 * @param err The thrown value.
 */
export function describeTotpSetupStartError(err: unknown): string {
  return describeTotpSetupError(err, TOTP_SETUP_START_FALLBACK);
}
