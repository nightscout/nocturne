/** Fallback shown when a submission fails for a reason we can't safely surface. */
export const GENERIC_SUBMIT_ERROR =
  "We couldn't save your changes. Please try again.";

/**
 * Turns a rejected form submission into a message for the user.
 *
 * A remote `form()` handler that throws `error(status, message)` rejects the
 * client-side `submit()` with an `HttpError`, whose body carries the handler's
 * message. Those messages are written for the user, so a 4xx message is shown
 * verbatim; anything else (5xx, network failure, thrown `Error`) falls back to
 * {@link GENERIC_SUBMIT_ERROR} so internals aren't rendered.
 */
export function describeSubmitError(err: unknown, fallback = GENERIC_SUBMIT_ERROR): string {
  const status = (err as { status?: unknown } | null)?.status;
  const body = (err as { body?: { message?: unknown } } | null)?.body;

  if (
    typeof status === "number" &&
    status >= 400 &&
    status < 500 &&
    typeof body?.message === "string" &&
    body.message.trim() !== ""
  ) {
    return body.message;
  }

  return fallback;
}
