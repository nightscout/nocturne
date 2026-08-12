/**
 * Message to show for a rejected remote function call.
 *
 * SvelteKit rethrows the generated remote function's `error(status, message)` as
 * an `HttpError` — a plain `{ status, body: { message } }` object, not an
 * `Error`. A scope refusal is a bare `ForbidResult` with no body, so the 403
 * message is the HTTP client's own boilerplate; the caller's fallback names the
 * permission instead.
 */
export function remoteErrorMessage(err: unknown, fallback: string): string {
  const e = err as { status?: number; body?: { message?: unknown } } | null;
  if (e?.status === 403) return fallback;

  const message = e?.body?.message;
  return typeof message === "string" && message.trim() ? message : fallback;
}
