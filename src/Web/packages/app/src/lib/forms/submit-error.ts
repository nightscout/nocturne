/** Fallback shown when a submission fails for a reason we can't safely surface. */
export const GENERIC_SUBMIT_ERROR =
  "We couldn't save your changes. Please try again.";

/** The HTTP status carried by a thrown value, if it has one. */
export function errorStatus(err: unknown): number | undefined {
  if (err && typeof err === "object" && "status" in err) {
    const { status } = err;
    if (typeof status === "number") return status;
  }
  return undefined;
}

/**
 * The message a remote handler put in `error(status, message)`. SvelteKit
 * delivers it as `HttpError.body.message`.
 */
export function errorMessage(err: unknown): string | undefined {
  if (!err || typeof err !== "object" || !("body" in err)) return undefined;

  const { body } = err;
  if (!body || typeof body !== "object" || !("message" in body)) return undefined;

  const { message } = body;
  if (typeof message !== "string" || message.trim() === "") return undefined;

  return message;
}

/**
 * Turns a rejected form submission into a message for the user.
 *
 * A remote `form()` handler that throws `error(status, message)` rejects the
 * client-side `submit()` with an `HttpError`, whose body carries the handler's
 * message. Those messages are written for the user, so a 4xx message is shown
 * verbatim; anything else (5xx, network failure, thrown `Error`) falls back to
 * {@link GENERIC_SUBMIT_ERROR} so internals aren't rendered.
 */
export function describeSubmitError(
  err: unknown,
  fallback = GENERIC_SUBMIT_ERROR
): string {
  const status = errorStatus(err);
  if (status !== undefined && status >= 400 && status < 500) {
    return errorMessage(err) ?? fallback;
  }

  return fallback;
}
