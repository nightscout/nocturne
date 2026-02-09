import type { PageServerLoad, Actions } from "./$types";
import { redirect, error } from "@sveltejs/kit";
import { getClientInfo } from "../oauth.remote";

export const load: PageServerLoad = async ({ url, locals }) => {
  // The OAuth authorize endpoint redirects here only after confirming
  // the user is authenticated. Redirect to login if the session expired.
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  const clientId = url.searchParams.get("client_id");
  const redirectUri = url.searchParams.get("redirect_uri");
  const scope = url.searchParams.get("scope");
  const state = url.searchParams.get("state") ?? "";
  const codeChallenge = url.searchParams.get("code_challenge");

  if (!clientId || !redirectUri || !scope || !codeChallenge) {
    throw error(400, "Missing required OAuth parameters.");
  }

  // Fetch client info using the remote function
  const clientInfo = await getClientInfo({ clientId });

  // Optional: previously-approved scopes for scope upgrade flows
  const existingScopes = url.searchParams.get("existing_scopes") ?? "";

  return {
    clientId,
    redirectUri,
    scope,
    state,
    codeChallenge,
    clientInfo: {
      clientId: clientInfo.clientId ?? clientId,
      displayName: clientInfo.displayName ?? null,
      isKnown: clientInfo.isKnown ?? false,
      homepage: clientInfo.homepage ?? null,
    },
    existingScopes,
  };
};

export const actions: Actions = {
  default: async ({ request, fetch, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const { apiClient } = locals;

    // Build URL-encoded body for the OAuth authorize endpoint
    const body = new URLSearchParams();
    body.set("client_id", (formData.get("client_id") as string) ?? "");
    body.set("redirect_uri", (formData.get("redirect_uri") as string) ?? "");
    body.set("scope", (formData.get("scope") as string) ?? "");
    body.set("state", (formData.get("state") as string) ?? "");
    body.set("code_challenge", (formData.get("code_challenge") as string) ?? "");
    body.set("approved", (formData.get("approved") as string) ?? "false");

    // Use the apiClient's baseUrl but make a direct fetch with redirect: "manual"
    // because the NSwag client doesn't support capturing 302 redirects
    const response = await fetch(`${apiClient.baseUrl}/oauth/authorize`, {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: body.toString(),
      redirect: "manual",
    });

    // The backend returns a 302 redirect:
    //   - On approve: redirect to redirect_uri with ?code=...&state=...
    //   - On deny: redirect to redirect_uri with ?error=access_denied&...
    if (response.status === 302 || response.status === 301) {
      const location = response.headers.get("Location");
      if (location) {
        throw redirect(302, location);
      }
    }

    if (response.status === 400) {
      const errorBody = await response.json().catch(() => null);
      throw error(
        400,
        errorBody?.errorDescription ?? "Invalid authorization request."
      );
    }

    if (response.status === 401) {
      throw redirect(303, "/auth/login");
    }

    throw error(500, "Unexpected response from authorization server.");
  },
};
