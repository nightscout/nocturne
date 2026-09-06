import { errorStatus } from "$lib/forms/submit-error";

/**
 * Why a recovery-code sign-in didn't succeed.
 *
 * - `rejected` — the username or the code was refused. The API answers the same
 *   way for both, so this cannot be narrowed further.
 * - `rate-limited` — too many attempts from this address. The limiter turns the
 *   attempt away before the code is read, so a correct code is still unspent;
 *   reported as `rejected` it would tell the holder of a working code, on the one
 *   path that exists for an emergency, that their last way in is dead.
 */
export type RecoveryFailure = "rejected" | "rate-limited";

export function classifyRecoveryError(err: unknown): RecoveryFailure {
  return errorStatus(err) === 429 ? "rate-limited" : "rejected";
}
