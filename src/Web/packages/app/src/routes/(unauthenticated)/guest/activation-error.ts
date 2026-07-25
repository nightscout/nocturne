import { errorStatus } from "$lib/forms/submit-error";

/**
 * Why a guest-code activation didn't succeed.
 *
 * - `rejected` — the code itself was refused. The API answers the same way for
 *   expired, revoked, already-used and mistyped codes.
 * - `rate-limited` — too many attempts from this address.
 * - `unavailable` — we couldn't get an answer: a server fault or a transport
 *   failure. Distinct from `rejected` so a broken backend is never reported to
 *   the reader as a bad code.
 */
export type ActivationFailure = "rejected" | "rate-limited" | "unavailable";

export function classifyActivationError(err: unknown): ActivationFailure {
  const status = errorStatus(err);

  if (status === 429) return "rate-limited";
  if (status === 400) return "rejected";

  // The generated client throws the parsed response body for a status it has a
  // response type for, so a refused code arrives as the API's
  // `{ expiresAt, error }` shape with no status attached. Everything else
  // arrives as an exception that does carry one.
  if (status === undefined && hasErrorField(err)) return "rejected";

  return "unavailable";
}

function hasErrorField(err: unknown): boolean {
  if (!err || typeof err !== "object" || !("error" in err)) return false;
  const { error } = err;
  return typeof error === "string" || error === null;
}
