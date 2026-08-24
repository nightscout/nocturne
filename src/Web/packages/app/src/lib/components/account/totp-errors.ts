/**
 * Turns a refused authenticator setup into something a person can act on.
 *
 * The server names the check that refused — `TotpSetupFailure` — and never the
 * wording, so the copy lives here. `verify-setup` carries the value as the
 * `detail` of its 400, which the generated remote wrapper forwards as the
 * thrown `HttpError`'s `body.message`.
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
};

function setupFailure(err: unknown): TotpSetupFailure | undefined {
  const body = errorMessage(err)?.trim();
  return body !== undefined && body in SETUP_FAILURES
    ? (body as TotpSetupFailure)
    : undefined;
}

/**
 * @param err The thrown value.
 * @param fallback Shown when nothing more specific applies.
 */
export function describeTotpSetupError(
  err: unknown,
  fallback = "Verification failed. Check the code and try again."
): string {
  const failure = setupFailure(err);
  if (failure !== undefined) return SETUP_FAILURES[failure];

  // Every 400 from this endpoint is a failure code, so one this build doesn't
  // know is not copy — showing it verbatim, as describeSubmitError would, puts
  // an identifier in front of the user.
  if (errorStatus(err) === 400) return fallback;

  return describeSubmitError(err, fallback);
}
