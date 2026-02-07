import type { PageServerLoad, Actions } from "./$types";
import { redirect, error, fail } from "@sveltejs/kit";
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

export const load: PageServerLoad = async ({ url, locals }) => {
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  const userCode = url.searchParams.get("user_code") ?? null;

  return {
    prefilledCode: userCode,
  };
};

export const actions: Actions = {
  lookup: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string)?.trim() ?? "";

    if (!userCode) {
      return fail(400, {
        action: "lookup" as const,
        error: "Please enter a device code.",
        userCode,
      });
    }

    const apiBaseUrl = getApiBaseUrl();
    const infoUrl = `${apiBaseUrl}/oauth/device-info?user_code=${encodeURIComponent(userCode)}`;

    try {
      const response = await fetch(infoUrl, {
        headers: buildBackendHeaders(cookies),
      });

      if (response.ok) {
        const data = await response.json();
        return {
          action: "lookup" as const,
          deviceInfo: {
            userCode: data.userCode ?? userCode,
            clientId: data.clientId ?? "",
            displayName: data.displayName ?? null,
            isKnown: data.isKnown ?? false,
            homepage: data.homepage ?? null,
            scopes: data.scopes ?? [],
          },
        };
      }

      if (response.status === 404 || response.status === 400) {
        const body = await response.json().catch(() => null);
        return fail(400, {
          action: "lookup" as const,
          error:
            body?.errorDescription ??
            "Invalid or expired device code. Please check the code and try again.",
          userCode,
        });
      }

      return fail(500, {
        action: "lookup" as const,
        error: "An unexpected error occurred. Please try again.",
        userCode,
      });
    } catch (e) {
      console.error("Failed to look up device code:", e);
      return fail(500, {
        action: "lookup" as const,
        error: "Could not reach the authorization server. Please try again.",
        userCode,
      });
    }
  },

  approve: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string) ?? "";
    const apiBaseUrl = getApiBaseUrl();

    const body = new URLSearchParams();
    body.set("user_code", userCode);
    body.set("approved", "true");

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/device-approve`, {
        method: "POST",
        headers: buildBackendHeaders(cookies, "application/x-www-form-urlencoded"),
        body: body.toString(),
      });

      if (response.ok || response.status === 204) {
        return { action: "approve" as const, success: true };
      }

      if (response.status === 400 || response.status === 404) {
        const errorBody = await response.json().catch(() => null);
        return fail(400, {
          action: "approve" as const,
          error:
            errorBody?.errorDescription ??
            "The device code has expired or is no longer valid.",
        });
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      return fail(500, {
        action: "approve" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e; // re-throw redirects
      console.error("Failed to approve device:", e);
      return fail(500, {
        action: "approve" as const,
        error: "Could not reach the authorization server. Please try again.",
      });
    }
  },

  deny: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const userCode = (formData.get("user_code") as string) ?? "";
    const apiBaseUrl = getApiBaseUrl();

    const body = new URLSearchParams();
    body.set("user_code", userCode);
    body.set("approved", "false");

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/device-approve`, {
        method: "POST",
        headers: buildBackendHeaders(cookies, "application/x-www-form-urlencoded"),
        body: body.toString(),
      });

      if (response.ok || response.status === 204) {
        return { action: "deny" as const, denied: true };
      }

      if (response.status === 400 || response.status === 404) {
        const errorBody = await response.json().catch(() => null);
        return fail(400, {
          action: "deny" as const,
          error:
            errorBody?.errorDescription ??
            "The device code has expired or is no longer valid.",
        });
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      return fail(500, {
        action: "deny" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e; // re-throw redirects
      console.error("Failed to deny device:", e);
      return fail(500, {
        action: "deny" as const,
        error: "Could not reach the authorization server. Please try again.",
      });
    }
  },
};
