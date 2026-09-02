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
 * of its own two `ApiException` messages, and an RFC-7807 refusal carrying no
 * `detail` reaches the 403 arm as its `title` — the status phrase. Only a
 * `Problem(detail: …)` refusal — the per-record scope guards — puts a sentence
 * written for a person in the body.
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
 * How a surface answers a rejected remote function whose reason it cannot show
 * verbatim.
 *
 * SvelteKit rethrows a generated remote function's `error(status, message)` as
 * an `HttpError` — a plain `{ status, body: { message } }` object with no
 * `Error` in its prototype chain — and every surface in the app reads it
 * through {@link describeRemoteError}, under one of the three policies below.
 * They agree on 429 and on a 4xx the server worded, and differ on four points.
 *
 * Which one a call site wants follows from the operation, not from the sentence
 * it passes: `describeSubmitError` is the writing half, for a command, form or
 * any other mutation, where a person is mid-task; `remoteErrorMessage`
 * (`$lib/api/remote-error`) is the reading half, for a query whose rejection is
 * rendered in place of the data it was going to show.
 *
 * The fallback sentence is the usual tell — a mutation's names the action the
 * user took ("Failed to create invite"), a query's names the permission or
 * scope the call needs ("Changing alerts requires alerts.readwrite") — but only
 * a tell. A mutation on a page whose controls are gated on a permission passes
 * that permission's sentence and still takes the writing half, because what
 * must not reach the user is a server's internal 5xx text, and that follows
 * from the operation alone. Only the two answers that would otherwise be the
 * caller's own sentence follow the sentence, which is what
 * {@link PERMISSION_GATED_MUTATION} is for; the two named exports cover the
 * rest.
 *
 * - `missing` (404). The codegen forwards a 404 with a fixed reason, so its
 *   message names the status rather than what was missing. A writer's fallback
 *   usually says which action failed, so it wins; a reader's is rendered where
 *   the data would have been, and a permission sentence there would tell
 *   someone who holds the permission that they lack it the moment they act on
 *   an id someone else deleted, so the reader answers with
 *   {@link MISSING_ITEM_ERROR} instead. A caller that can say something better
 *   ("already removed") reads the status itself.
 * - `forbiddenReason` (403). A writer's fallback ("You don't have permission to
 *   save this") beats the client's own boilerplate but loses to a refusal the
 *   server worded, so the writer takes a server-written body when there is one
 *   — see {@link CLIENT_WRITTEN_FORBIDDEN}. A reader's fallback names the exact
 *   permission and beats any body the server could send, so the reader always
 *   takes the fallback. A bare `ForbidResult` — what `[RequireScope]` produces,
 *   and so the common refusal — is client-written either way, so both halves
 *   answer it with the caller's sentence.
 * - `serverError` (5xx, a network failure, a thrown `Error` — anything that is
 *   not a 4xx). A person mid-task must not be shown a server's internal text,
 *   so the writer suppresses it; for a reader the body is the only clue why a
 *   panel is empty, so the reader surfaces it.
 * - `fault`, the answer once a non-4xx is suppressed. Same reasoning as
 *   `missing`, on the other axis: a writer's fallback names the action, which
 *   is what failed, so it wins; a permission sentence would tell someone who
 *   holds the permission that they lack it because the database hiccupped, so a
 *   caller passing one answers with {@link GENERIC_SUBMIT_ERROR} instead.
 *
 * `missing` and `fault` are therefore the two axes the fallback sentence rather
 * than the operation decides, and the two {@link PERMISSION_GATED_MUTATION}
 * sets.
 *
 * A 429 answers with {@link RATE_LIMITED_ERROR} ahead of all of them, because
 * the rate limiter's body carries no `message` and a caller's fallback
 * describes what it asked for — "this invite link is invalid" for a request the
 * limiter never let reach the invite.
 */
export interface RemoteErrorPolicy {
  /** Answer for a 404, or the caller's fallback when absent. */
  readonly missing?: string;
  /** Whether a 403 the server worded itself outranks the caller's fallback. */
  readonly forbiddenReason: boolean;
  /** Whether a body that did not come with a 4xx outranks the caller's fallback. */
  readonly serverError: boolean;
  /**
   * Answer once a body that did not come with a 4xx is suppressed, or the
   * caller's fallback when absent. Read only when `serverError` is false.
   */
  readonly fault?: string;
}

const WRITE_SURFACE: RemoteErrorPolicy = {
  forbiddenReason: true,
  serverError: false,
};

/**
 * The writing half, for a mutation whose fallback names the permission its page
 * is gated on rather than the action. Everywhere the write half would answer
 * with that sentence and the failure is not a refusal — a stale id, a server
 * fault — it would tell someone who holds the permission that they lack it, so
 * those two answers are fixed instead. Reach it through
 * {@link permissionGatedMutationError}; `describeSubmitError` covers every
 * mutation whose fallback names the action.
 */
export const PERMISSION_GATED_MUTATION: RemoteErrorPolicy = {
  missing: MISSING_ITEM_ERROR,
  forbiddenReason: true,
  serverError: false,
  fault: GENERIC_SUBMIT_ERROR,
};

/**
 * Message for a rejected mutation whose fallback names the missing permission.
 * @see PERMISSION_GATED_MUTATION
 */
export const permissionGatedMutationError = (
  err: unknown,
  fallback: string
): string => describeRemoteError(err, fallback, PERMISSION_GATED_MUTATION);

/** @see RemoteErrorPolicy */
export const READ_SURFACE: RemoteErrorPolicy = {
  missing: MISSING_ITEM_ERROR,
  forbiddenReason: false,
  serverError: true,
};

/** Turns a rejected remote function into a message for the user. */
export function describeRemoteError(
  err: unknown,
  fallback: string,
  policy: RemoteErrorPolicy
): string {
  const status = errorStatus(err);
  if (status === 429) return RATE_LIMITED_ERROR;
  if (status === 404) return policy.missing ?? fallback;
  if (status === 403) {
    return (
      (policy.forbiddenReason ? forbiddenReason(err) : undefined) ?? fallback
    );
  }

  const is4xx = status !== undefined && status >= 400 && status < 500;
  if (is4xx || policy.serverError) return errorMessage(err) ?? fallback;

  return policy.fault ?? fallback;
}

/**
 * Turns a rejected form submission into a message for the user, on the writing
 * half of {@link RemoteErrorPolicy}.
 */
export function describeSubmitError(
  err: unknown,
  fallback = GENERIC_SUBMIT_ERROR
): string {
  return describeRemoteError(err, fallback, WRITE_SURFACE);
}
