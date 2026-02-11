import type { PageServerLoad } from "./$types";
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
