import {
  MISSING_ITEM_ERROR,
  RATE_LIMITED_ERROR,
} from "$lib/forms/submit-error";

/**
 * Message to show for a rejected remote function call.
 *
 * SvelteKit rethrows the generated remote function's `error(status, message)` as
 * an `HttpError` — a plain `{ status, body: { message } }` object, not an
 * `Error`. A scope refusal is a bare `ForbidResult` with no body, so the 403
 * message is the HTTP client's own boilerplate; the caller's fallback names the
 * permission instead. A 429 is answered from the status for the same reason the
 * codegen forwards it with a fixed reason, and because a fallback naming the
 * permission the caller lacks describes the wrong refusal.
 *
 * A 404 is answered from the status as well, but with {@link MISSING_ITEM_ERROR}
 * rather than the fallback: the codegen forwards it with a fixed reason, so its
 * message names the status, and every caller here passes a sentence about a
 * missing permission — which a caller who holds the permission would be told
 * they lack the moment they act on an id someone else deleted.
 *
 * `describeSubmitError` (`$lib/forms/submit-error`) reads the same
 * `HttpError` shape for the other
 * half of the app, and the two differ on three points. Which one a call site
 * wants follows from what its fallback sentence says:
 *
 * - Use this one where the fallback names the permission or scope the call
 *   needs ("Changing alerts requires alerts.readwrite") — the reading surfaces,
 *   where a refusal is the expected failure and a 5xx body is the only clue why
 *   a panel is empty. It forwards a 5xx body, and always answers 403 with the
 *   fallback, which names the missing permission more precisely than any body
 *   the server could send.
 * - Use `describeSubmitError` where the fallback names the action the user took
 *   ("Failed to create invite") — the writing surfaces, where the person is
 *   mid-task and a server's internal 5xx text must not reach them. It suppresses
 *   a 5xx body, answers 404 with the fallback, and answers 403 with the fallback
 *   unless the server wrote the body rather than the HTTP client.
 */
export function remoteErrorMessage(err: unknown, fallback: string): string {
  const e = err as { status?: number; body?: { message?: unknown } } | null;
  if (e?.status === 429) return RATE_LIMITED_ERROR;
  if (e?.status === 404) return MISSING_ITEM_ERROR;
  if (e?.status === 403) return fallback;

  const message = e?.body?.message;
  return typeof message === "string" && message.trim() ? message : fallback;
}
