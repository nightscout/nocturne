/**
 * Remote functions for the bot authorize flow.
 *
 * Only contains server-side logic that can't be covered by the generated
 * chatidentities remote functions (process.env access, apex detection).
 * For API calls, use $lib/api/generated/chatIdentities.generated.remote.ts.
 */
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";

/**
 * Get the bot authorize page context — detects whether we're on the apex
 * domain (slug-prompt mode) or a tenant subdomain.
 */
export const getBotAuthorizeContext = query(async () => {
  const { url, locals } = getRequestEvent();

  const baseDomain = process.env.BASE_DOMAIN;
  if (baseDomain) {
    const currentHost = url.host.toLowerCase();
    const expectedApex = baseDomain.toLowerCase();
    if (currentHost === expectedApex) {
      return {
        mode: "slug-prompt" as const,
        baseDomain,
        isAuthenticated: locals.isAuthenticated,
      };
    }
  }

  return {
    mode: "tenant" as const,
    baseDomain: baseDomain ?? null,
    isAuthenticated: locals.isAuthenticated,
  };
});

/**
 * Build the redirect URL for the slug-prompt flow.
 * Needs process.env.BASE_DOMAIN which is server-only.
 */
export const buildTenantRedirectUrl = query(
  z.object({
    slug: z.string(),
    stateToken: z.string(),
  }),
  async ({ slug, stateToken }) => {
    const trimmedSlug = slug.trim().toLowerCase();

    if (!trimmedSlug || !/^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]?$/.test(trimmedSlug)) {
      return { error: "Please enter a valid instance slug (letters, digits, hyphens)." };
    }

    const baseDomain = process.env.BASE_DOMAIN;
    if (!baseDomain) {
      return { error: "Server misconfigured: BASE_DOMAIN not set." };
    }

    return {
      url: `https://${trimmedSlug}.${baseDomain}/auth/bot/authorize?state=${stateToken}`,
    };
  }
);
