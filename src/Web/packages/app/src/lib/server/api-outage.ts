import { errorStatus } from "#lib/forms/submit-error.js";

/**
 * Whether a failed API call says nothing about the caller: the API was unreachable, timed out or
 * answered 5xx. Any other status is the API's own verdict (no session, not permitted, gone).
 */
export function isApiOutage(error: unknown): boolean {
  const status = errorStatus(error);
  return status === undefined || status >= 500;
}
