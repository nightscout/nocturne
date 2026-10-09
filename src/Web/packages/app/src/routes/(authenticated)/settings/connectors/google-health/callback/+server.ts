import { redirect } from "@sveltejs/kit";
import type { RequestHandler } from "./$types";

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
      } catch {
        // Authorization codes, tokens and provider errors must never be logged.
      }
    }
  }

  redirect(303, `/settings/connectors/google-health?connection=${outcome}`);
};
