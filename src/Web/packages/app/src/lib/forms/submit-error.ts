import type { RustValidationIssue } from "#lib/api/generated/nocturne-api-client.js";
import { parseCode, parseIssues } from "#lib/api/error-body.js";
import { recordApiFailure } from "#lib/support/api-failure-log.js";
import {
  genericSubmitError,
  missingItemError,
  missingScopeError,
  rateLimitedError,
} from "./submit-messages.svelte";

/**
 * The code of the API's scope refusal. Its detail names the scope, so it is never
 * shown. A writer answers it with {@link missingScopeError} unless its policy
 * sets `ownRefusalSentence`.
 */
const MISSING_SCOPE_CODE = "missing_scope";

/** The HTTP status carried by a thrown value, if it has one. */
export function errorStatus(err: unknown): number | undefined {
  if (err && typeof err === "object" && "status" in err) {
    const { status } = err;
    if (typeof status === "number") return status;
  }
  return undefined;
}

/**
 * The `App.Error` behind a rejection: the `body` of a thrown `HttpError`, or the error itself where
 * SvelteKit hands it over unwrapped, as a query's `error` does.
 */
function errorBody(err: unknown): object | undefined {
  if (!err || typeof err !== "object") return undefined;

  if ("body" in err) {
    const { body } = err;
    return body && typeof body === "object" ? body : undefined;
  }

  if (!(err instanceof Error) && "status" in err && typeof err.status === "number") return err;

  return undefined;
}

/**
 * The message a remote handler put in `error(status, message)`. SvelteKit
 * delivers it as `HttpError.body.message`, or as `message` on a query's `error`.
 */
export function errorMessage(err: unknown): string | undefined {
  const body = errorBody(err);
  if (body === undefined || !("message" in body)) return undefined;

  const { message } = body;
  if (typeof message !== "string" || message.trim() === "") return undefined;

  return message;
}

/**
 * The structured validation issues a body carries, when every entry has the
 * shape one declares.
 */
export function errorIssues(
  err: unknown
): RustValidationIssue[] | undefined {
  const body = errorBody(err);
  if (body === undefined || !("issues" in body)) return undefined;

  return parseIssues(body.issues);
}

/**
 * The stable problem code a rejection carries, when the server sent one in the
 * shape the API writes its codes in.
 */
export function errorCode(err: unknown): string | undefined {
  const body = errorBody(err);
  if (body === undefined || !("code" in body)) return undefined;

  return parseCode(body.code);
}

/**
 * The sentences a call site has for the problem codes it knows, keyed by code.
 * A code outside the map, or none, leaves the answer to {@link RemoteErrorPolicy}.
 */
export type ProblemCodeMessages = Readonly<Record<string, string>>;

/**
 * The bodies the client wrote itself rather than carried from the server.
 *
 * A refusal by the demo-subject guard is a bare `ForbidResult`, which NSwag
 * reports with one of its own two `ApiException` messages, and an RFC-7807 refusal carrying no
 * `detail` reaches the 403 arm as its `title` — the status phrase. A query
 * refused on a share host carries the codegen's own 401 phrase. Only a
 * `Problem(detail: …)` refusal — the per-record scope guards — puts a sentence
 * written for a person in the body. NSwag reports an undeclared status the same
 * way at any status, so a bare `BadRequest()` carries one of those two messages
 * under a 400 — which is why every arm that forwards a body consults this,
 * rather than the non-4xx one alone.
 *
 * SvelteKit adds "failed to execute remote function" when a *query*'s
 * transport answers non-2xx. A command or form rejects with a plain `Error`
 * carrying no body, so none of these checks ever sees one. The codegen's raw
 * fetch path, used for a form-encoded operation, writes "Request failed (NNN)"
 * and carries it to `body.message` whenever the body names no reason of its
 * own.
 *
 * Recognising the synthesized side is the only check that can be complete: these
 * strings are constants of a pinned client, of SvelteKit and of our own codegen,
 * whose 401 and 403 arms fall back to the status phrase. What a server may write is
 * unbounded and so cannot be matched positively.
 */
const CLIENT_WRITTEN_REASONS: readonly RegExp[] = [
  /^an unexpected server error occurred\.$/i,
  /^a server side error occurred\.$/i,
  /^forbidden$/i,
  /^unauthorized$/i,
  /^failed to execute remote function$/i,
  /^request failed \(\d{3}\)$/i,
];

/** The body, when the server rather than the client wrote it. */
function serverWrittenReason(err: unknown): string | undefined {
  const message = errorMessage(err);
  if (message === undefined) return undefined;

  const trimmed = message.trim();
  return CLIENT_WRITTEN_REASONS.some((reason) => reason.test(trimmed))
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
 * (`#lib/api/remote-error.js`) is the reading half, for a query whose rejection is
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
 *   {@link missingItemError} instead. A caller that can say something better
 *   ("already removed") reads the status itself.
 * - `forbiddenReason` (403). A writer's fallback ("You don't have permission to
 *   save this") beats the client's own boilerplate but loses to a refusal the
 *   server worded, so the writer takes a server-written body when there is one
 *   — see {@link CLIENT_WRITTEN_REASONS}. A reader's fallback names the exact
 *   permission and beats any body the server could send, so the reader always
 *   takes the fallback. A bare `ForbidResult` (the demo-subject guard) is
 *   client-written either way, so both halves answer it with the caller's
 *   sentence. A `missing_scope` refusal is coded rather than server-worded: a
 *   writer answers it with {@link missingScopeError} ahead of its fallback,
 *   unless `ownRefusalSentence` is set.
 * - `serverError` (5xx, a network failure, a thrown `Error` — anything that is
 *   not a 4xx). A person mid-task must not be shown a server's internal text,
 *   so the writer suppresses it; for a reader the body is the only clue why a
 *   panel is empty, so the reader surfaces it — but only where the server wrote
 *   it, the one condition both halves share; see
 *   {@link CLIENT_WRITTEN_REASONS}.
 * - `fault`, the answer once a non-4xx is suppressed. Same reasoning as
 *   `missing`, on the other axis: a writer's fallback names the action, which
 *   is what failed, so it wins; a permission sentence would tell someone who
 *   holds the permission that they lack it because the database hiccupped, so a
 *   caller passing one answers with {@link genericSubmitError} instead.
 *
 * `missing` and `fault` are therefore the two axes the fallback sentence rather
 * than the operation decides, and the two {@link PERMISSION_GATED_MUTATION}
 * sets.
 *
 * A 429 answers with {@link rateLimitedError} ahead of all of them, because
 * the rate limiter's body carries no `message` and a caller's fallback
 * describes what it asked for — "this invite link is invalid" for a request the
 * limiter never let reach the invite.
 */
export interface RemoteErrorPolicy {
  /** Answer for a 404, or the caller's fallback when absent. */
  readonly missing?: () => string;
  /** Whether a 403 the server worded itself outranks the caller's fallback. */
  readonly forbiddenReason: boolean;
  /**
   * Whether the caller's fallback is a refusal sentence of its own, which a
   * `missing_scope` 403 leaves in place of {@link missingScopeError}.
   */
  readonly ownRefusalSentence?: boolean;
  /**
   * Whether a server-written body that did not come with a 4xx outranks the
   * caller's fallback.
   * @see CLIENT_WRITTEN_REASONS
   */
  readonly serverError: boolean;
  /**
   * Answer once a body that did not come with a 4xx is suppressed, or the
   * caller's fallback when absent. Read only when `serverError` is false.
   */
  readonly fault?: () => string;
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
  missing: missingItemError,
  forbiddenReason: true,
  ownRefusalSentence: true,
  serverError: false,
  fault: genericSubmitError,
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
  missing: missingItemError,
  forbiddenReason: false,
  serverError: true,
};

/** Turns a rejected remote function into a message for the user. */
export function describeRemoteError(
  err: unknown,
  fallback: string,
  policy: RemoteErrorPolicy,
  codes?: ProblemCodeMessages
): string {
  const status = errorStatus(err);
  const described = describe(err, fallback, policy, status, codes);

  // Every rejected API call the user is told about passes through here, and the
  // transport carries no path or correlation id, so this is the only place the
  // support form can learn what went wrong before the report was written.
  recordApiFailure(status, described);

  return described;
}

function describe(
  err: unknown,
  fallback: string,
  policy: RemoteErrorPolicy,
  status: number | undefined,
  codes: ProblemCodeMessages | undefined
): string {
  if (status === 429) return rateLimitedError();

  const code = errorCode(err);
  if (code !== undefined && codes !== undefined && Object.hasOwn(codes, code)) {
    return codes[code];
  }

  if (status === 404) return policy.missing?.() ?? fallback;
  if (status === 403) {
    if (policy.forbiddenReason && code === MISSING_SCOPE_CODE) {
      return policy.ownRefusalSentence ? fallback : missingScopeError();
    }
    return (
      (policy.forbiddenReason ? serverWrittenReason(err) : undefined) ??
      fallback
    );
  }

  const is4xx = status !== undefined && status >= 400 && status < 500;
  if (is4xx || policy.serverError) return serverWrittenReason(err) ?? fallback;

  return policy.fault?.() ?? fallback;
}

/**
 * Turns a rejected form submission into a message for the user, on the writing
 * half of {@link RemoteErrorPolicy}. `codes` holds the sentences the caller has
 * for the problem codes it expects.
 */
export function describeSubmitError(
  err: unknown,
  fallback = genericSubmitError(),
  codes?: ProblemCodeMessages
): string {
  return describeRemoteError(err, fallback, WRITE_SURFACE, codes);
}
