import type { PageServerLoad, Actions } from "./$types";
import { redirect, error } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import { env as publicEnv } from "$env/dynamic/public";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { createHash } from "crypto";

/**
 * Get the API base URL from environment configuration.
 */
function getApiBaseUrl(): string {
  const url = env.NOCTURNE_API_URL || publicEnv.PUBLIC_API_URL;
  if (!url) {
    throw error(500, "API URL not configured");
  }
  return url;
}

/**
 * Get the hashed API secret for backend authentication.
 */
function getHashedApiSecret(): string | null {
  const apiSecret = env.API_SECRET;
  return apiSecret
    ? createHash("sha1").update(apiSecret).digest("hex").toLowerCase()
    : null;
}

/**
 * Build request headers that include auth cookies and the API secret,
 * matching the pattern used by the proxy handler in hooks.server.ts.
 */
function buildBackendHeaders(
  cookies: import("@sveltejs/kit").Cookies,
  contentType?: string
): Record<string, string> {
  const headers: Record<string, string> = {};

  if (contentType) {
    headers["Content-Type"] = contentType;
  }

  // Forward auth cookies
  const accessToken = cookies.get(AUTH_COOKIE_NAMES.accessToken);
  const refreshToken = cookies.get(AUTH_COOKIE_NAMES.refreshToken);
  const cookieParts: string[] = [];
  if (accessToken) {
    cookieParts.push(`${AUTH_COOKIE_NAMES.accessToken}=${accessToken}`);
  }
  if (refreshToken) {
    cookieParts.push(`${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}`);
  }
  if (cookieParts.length > 0) {
    headers["Cookie"] = cookieParts.join("; ");
  }

  // Include API secret if configured
  const hashedSecret = getHashedApiSecret();
  if (hashedSecret) {
    headers["api-secret"] = hashedSecret;
  }

  return headers;
}

export const load: PageServerLoad = async ({ url, locals, cookies }) => {
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

  // Fetch client info from the backend
  const apiBaseUrl = getApiBaseUrl();
  const clientInfoUrl = `${apiBaseUrl}/oauth/client-info?client_id=${encodeURIComponent(clientId)}`;

  let clientInfo: {
    clientId: string;
    displayName: string | null;
    isKnown: boolean;
    homepage: string | null;
  } = {
    clientId,
    displayName: null,
    isKnown: false,
    homepage: null,
  };

  try {
    const response = await fetch(clientInfoUrl, {
      headers: buildBackendHeaders(cookies),
    });
    if (response.ok) {
      const data = await response.json();
      clientInfo = {
        clientId: data.clientId ?? clientId,
        displayName: data.displayName ?? null,
        isKnown: data.isKnown ?? false,
        homepage: data.homepage ?? null,
      };
    }
  } catch (e) {
    console.error("Failed to fetch client info:", e);
  }

  return {
    clientId,
    redirectUri,
    scope,
    state,
    codeChallenge,
    clientInfo,
  };
};

export const actions: Actions = {
  default: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const apiBaseUrl = getApiBaseUrl();

    // Build URL-encoded body matching ConsentApprovalRequest field names
    const body = new URLSearchParams();
    body.set("client_id", (formData.get("client_id") as string) ?? "");
    body.set("redirect_uri", (formData.get("redirect_uri") as string) ?? "");
    body.set("scope", (formData.get("scope") as string) ?? "");
    body.set("state", (formData.get("state") as string) ?? "");
    body.set("code_challenge", (formData.get("code_challenge") as string) ?? "");
    body.set("approved", (formData.get("approved") as string) ?? "false");

    const response = await fetch(`${apiBaseUrl}/oauth/authorize`, {
      method: "POST",
      headers: buildBackendHeaders(cookies, "application/x-www-form-urlencoded"),
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
