/** Fallback shown when a submission fails for a reason we can't safely surface. */
export const GENERIC_SUBMIT_ERROR =
  "We couldn't save your changes. Please try again.";

/** Shown when a rate limiter turned the attempt away, so the credential is unspent. */
export const RATE_LIMITED_ERROR =
  "Too many attempts. Please wait a few minutes and try again.";

/** Shown when the thing an action referred to is no longer on the server. */
export const MISSING_ITEM_ERROR =
  "That item no longer exists. Refresh the page to see what's there now.";

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
 * The 403 bodies the client wrote itself rather than carried from the server.
 *
 * A scope refusal is usually a bare `ForbidResult`, which NSwag reports with one
 * of its own two `ApiException` messages, and the codegen's 403 arm falls back
 * to the status phrase when the thrown value carries neither a message nor a
 * detail. Only a `Problem(detail: …)` refusal — the per-record scope guards —
 * puts a sentence written for a person in the body.
 *
 * Recognising the synthesized side is the only check that can be complete: these
 * three strings are constants of a pinned client and of our own codegen, while
 * what a server may write is unbounded and so cannot be matched positively.
 */
const CLIENT_WRITTEN_FORBIDDEN = new Set([
  "an unexpected server error occurred.",
  "a server side error occurred.",
  "forbidden",
]);

/** The 403 body, when the server rather than the client wrote it. */
function forbiddenReason(err: unknown): string | undefined {
  const message = errorMessage(err);
  if (message === undefined) return undefined;

  return CLIENT_WRITTEN_FORBIDDEN.has(message.trim().toLowerCase())
    ? undefined
    : message;
}

/**
 * Turns a rejected form submission into a message for the user.
 *
 * A remote `form()` handler that throws `error(status, message)` rejects the
 * client-side `submit()` with an `HttpError`, whose body carries the handler's
 * message. Those messages are written for the user, so a 4xx message is shown
 * verbatim; anything else (5xx, network failure, thrown `Error`) falls back to
 * {@link GENERIC_SUBMIT_ERROR} so internals aren't rendered.
 *
 * A 429 answers with {@link RATE_LIMITED_ERROR} ahead of either, because the
 * rate limiter's body carries no `message` and a caller's fallback describes
 * what it asked for — "this invite link is invalid" for a request the limiter
 * never let reach the invite.
 *
 * A 404 answers with the caller's fallback for the same reason: the codegen
 * forwards it with a fixed reason, so its message names the status rather than
 * what was missing. A caller that can say something better ("already removed")
 * reads the status itself.
 *
 * A 403 answers with the caller's fallback unless the server wrote the body —
 * see {@link CLIENT_WRITTEN_FORBIDDEN}. "You don't have permission to save
 * this" beats "A server side error occurred.", and loses only to a refusal that
 * names the scope.
 */
export function describeSubmitError(
  err: unknown,
  fallback = GENERIC_SUBMIT_ERROR
): string {
  const status = errorStatus(err);
  if (status === 429) return RATE_LIMITED_ERROR;
  if (status === 404) return fallback;
  if (status === 403) return forbiddenReason(err) ?? fallback;

  if (status !== undefined && status >= 400 && status < 500) {
    return errorMessage(err) ?? fallback;
  }

  return fallback;
}
