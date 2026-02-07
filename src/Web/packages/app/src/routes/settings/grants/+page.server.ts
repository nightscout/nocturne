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

export interface Grant {
  id: string;
  clientId: string;
  clientDisplayName: string | null;
  isKnownClient: boolean;
  grantType: string;
  scopes: string[];
  label: string | null;
  followerEmail: string | null;
  followerName: string | null;
  createdAt: string;
  lastUsedAt: string | null;
}

export interface Invite {
  id: string;
  scopes: string[];
  label: string | null;
  expiresAt: string;
  maxUses: number | null;
  useCount: number;
  createdAt: string;
  isValid: boolean;
  isExpired: boolean;
  isRevoked: boolean;
}

export const load: PageServerLoad = async ({ url, locals, cookies }) => {
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }

  const apiBaseUrl = getApiBaseUrl();
  let grants: Grant[] = [];
  let invites: Invite[] = [];

  try {
    // Fetch grants and invites in parallel
    const [grantsResponse, invitesResponse] = await Promise.all([
      fetch(`${apiBaseUrl}/oauth/grants`, {
        headers: buildBackendHeaders(cookies),
      }),
      fetch(`${apiBaseUrl}/oauth/invites`, {
        headers: buildBackendHeaders(cookies),
      }),
    ]);

    if (grantsResponse.ok) {
      grants = await grantsResponse.json();
    } else if (grantsResponse.status === 401) {
      throw redirect(303, "/auth/login");
    }

    if (invitesResponse.ok) {
      const invitesData = await invitesResponse.json();
      invites = invitesData.invites ?? [];
    }
  } catch (e) {
    if (e && typeof e === "object" && "status" in e) throw e;
    console.error("Failed to fetch grants/invites:", e);
  }

  return { grants, invites };
};

export const actions: Actions = {
  revoke: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const grantId = (formData.get("grant_id") as string) ?? "";

    if (!grantId) {
      return fail(400, { action: "revoke" as const, error: "Missing grant ID." });
    }

    const apiBaseUrl = getApiBaseUrl();

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/grants/${encodeURIComponent(grantId)}`, {
        method: "DELETE",
        headers: buildBackendHeaders(cookies),
      });

      if (response.ok || response.status === 204) {
        return { action: "revoke" as const, success: true };
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      if (response.status === 404) {
        return fail(404, {
          action: "revoke" as const,
          error: "Grant not found. It may have already been revoked.",
        });
      }

      return fail(500, {
        action: "revoke" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e;
      console.error("Failed to revoke grant:", e);
      return fail(500, {
        action: "revoke" as const,
        error: "Could not reach the server. Please try again.",
      });
    }
  },

  addFollower: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const followerEmail = (formData.get("follower_email") as string)?.trim() ?? "";
    const scopesRaw = (formData.get("scopes") as string) ?? "";
    const label = (formData.get("label") as string)?.trim() || null;

    if (!followerEmail) {
      return fail(400, {
        action: "addFollower" as const,
        error: "Email address is required.",
        followerEmail,
        label,
      });
    }

    if (!scopesRaw) {
      return fail(400, {
        action: "addFollower" as const,
        error: "At least one scope must be selected.",
        followerEmail,
        label,
      });
    }

    const scopes = scopesRaw.split(",").filter(Boolean);
    const apiBaseUrl = getApiBaseUrl();

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/grants/follower`, {
        method: "POST",
        headers: buildBackendHeaders(cookies, "application/json"),
        body: JSON.stringify({
          followerEmail,
          scopes,
          label,
        }),
      });

      if (response.ok) {
        return { action: "addFollower" as const, success: true };
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      if (response.status === 400) {
        const body = await response.json().catch(() => null);
        return fail(400, {
          action: "addFollower" as const,
          error: body?.errorDescription ?? body?.message ?? "Invalid request. Please check the details.",
          followerEmail,
          label,
        });
      }

      if (response.status === 409) {
        return fail(409, {
          action: "addFollower" as const,
          error: "A follower grant already exists for this email.",
          followerEmail,
          label,
        });
      }

      return fail(500, {
        action: "addFollower" as const,
        error: "An unexpected error occurred. Please try again.",
        followerEmail,
        label,
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e;
      console.error("Failed to add follower:", e);
      return fail(500, {
        action: "addFollower" as const,
        error: "Could not reach the server. Please try again.",
        followerEmail,
        label,
      });
    }
  },

  updateGrant: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const grantId = (formData.get("grant_id") as string) ?? "";
    const label = (formData.get("label") as string)?.trim() || null;
    const scopesRaw = (formData.get("scopes") as string) ?? "";
    const scopes = scopesRaw.split(",").filter(Boolean);

    if (!grantId) {
      return fail(400, { action: "updateGrant" as const, error: "Missing grant ID." });
    }

    const apiBaseUrl = getApiBaseUrl();

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/grants/${encodeURIComponent(grantId)}`, {
        method: "PATCH",
        headers: buildBackendHeaders(cookies, "application/json"),
        body: JSON.stringify({ label, scopes }),
      });

      if (response.ok) {
        return { action: "updateGrant" as const, success: true };
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      if (response.status === 404) {
        return fail(404, {
          action: "updateGrant" as const,
          error: "Grant not found.",
        });
      }

      if (response.status === 400) {
        const body = await response.json().catch(() => null);
        return fail(400, {
          action: "updateGrant" as const,
          error: body?.errorDescription ?? body?.message ?? "Invalid request.",
        });
      }

      return fail(500, {
        action: "updateGrant" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e;
      console.error("Failed to update grant:", e);
      return fail(500, {
        action: "updateGrant" as const,
        error: "Could not reach the server. Please try again.",
      });
    }
  },

  createInvite: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const scopesRaw = (formData.get("scopes") as string) ?? "";
    const label = (formData.get("label") as string)?.trim() || null;
    const expiresInDays = parseInt((formData.get("expires_in_days") as string) ?? "7", 10);
    const maxUsesRaw = formData.get("max_uses") as string;
    const maxUses = maxUsesRaw ? parseInt(maxUsesRaw, 10) : null;

    if (!scopesRaw) {
      return fail(400, {
        action: "createInvite" as const,
        error: "At least one scope must be selected.",
      });
    }

    const scopes = scopesRaw.split(",").filter(Boolean);
    const apiBaseUrl = getApiBaseUrl();

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/invites`, {
        method: "POST",
        headers: buildBackendHeaders(cookies, "application/json"),
        body: JSON.stringify({
          scopes,
          label,
          expiresInDays,
          maxUses,
        }),
      });

      if (response.ok) {
        const data = await response.json();
        return {
          action: "createInvite" as const,
          success: true,
          inviteUrl: data.inviteUrl,
          token: data.token,
        };
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      if (response.status === 400) {
        const body = await response.json().catch(() => null);
        return fail(400, {
          action: "createInvite" as const,
          error: body?.errorDescription ?? body?.message ?? "Invalid request.",
        });
      }

      return fail(500, {
        action: "createInvite" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e;
      console.error("Failed to create invite:", e);
      return fail(500, {
        action: "createInvite" as const,
        error: "Could not reach the server. Please try again.",
      });
    }
  },

  revokeInvite: async ({ request, cookies, locals }) => {
    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    const formData = await request.formData();
    const inviteId = (formData.get("invite_id") as string) ?? "";

    if (!inviteId) {
      return fail(400, { action: "revokeInvite" as const, error: "Missing invite ID." });
    }

    const apiBaseUrl = getApiBaseUrl();

    try {
      const response = await fetch(`${apiBaseUrl}/oauth/invites/${encodeURIComponent(inviteId)}`, {
        method: "DELETE",
        headers: buildBackendHeaders(cookies),
      });

      if (response.ok || response.status === 204) {
        return { action: "revokeInvite" as const, success: true };
      }

      if (response.status === 401) {
        throw redirect(303, "/auth/login");
      }

      return fail(500, {
        action: "revokeInvite" as const,
        error: "An unexpected error occurred. Please try again.",
      });
    } catch (e) {
      if (e && typeof e === "object" && "status" in e) throw e;
      console.error("Failed to revoke invite:", e);
      return fail(500, {
        action: "revokeInvite" as const,
        error: "Could not reach the server. Please try again.",
      });
    }
  },
};
