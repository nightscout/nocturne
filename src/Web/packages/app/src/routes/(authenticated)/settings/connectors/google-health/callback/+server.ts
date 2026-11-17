import { redirect } from "@sveltejs/kit";
import { parseErrorBody } from "#lib/api/error-body.js";
import {
  isGoogleHealthErrorCode,
  type GoogleHealthErrorCode,
} from "#lib/connectors/google-health-error-codes.js";
import type { RequestHandler } from "./$types";

/**
 * The problem `detail` the API answered with, when it is a code the page has a sentence for.
 * The body is read as in {@link parseErrorBody}.
 */
function failureCode(err: unknown): GoogleHealthErrorCode | "failed" {
  const thrown = err && typeof err === "object" && "detail" in err ? err.detail : undefined;
  const detail = typeof thrown === "string" ? thrown : parseErrorBody(err)?.detail;
  return detail !== undefined && isGoogleHealthErrorCode(detail) ? detail : "failed";
}

export const GET: RequestHandler = async ({ locals, url, setHeaders }) => {
  setHeaders({ "cache-control": "no-store", "referrer-policy": "no-referrer" });
  let outcome =
    !locals.isAuthenticated || !locals.user
      ? "no_session"
      : url.searchParams.has("error")
        ? "provider_denied"
        : "failed";

  if (locals.isAuthenticated && locals.user && !url.searchParams.has("error")) {
    const code = url.searchParams.get("code");
    const state = url.searchParams.get("state");
    if (code && state) {
      try {
        await locals.apiClient.googleHealth.completeGoogleHealth({ code, state });
        outcome = "connected";
      } catch (err) {
        // Authorization codes, tokens and provider errors must never be logged.
        outcome = failureCode(err);
      }
    }
  }

  redirect(303, `/settings/connectors/google-health?connection=${outcome}`);
};
