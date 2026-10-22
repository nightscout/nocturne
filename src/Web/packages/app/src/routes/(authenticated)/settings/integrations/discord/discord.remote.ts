/**
 * Remote functions for the Discord integration settings page.
 *
 * Only contains server-side logic that needs process.env or crypto.
 * For CRUD operations on chat identity links, use
 * $lib/api/generated/chatIdentities.generated.remote.ts.
 */
import { getRequestEvent, query, command } from "$app/server";
import { error } from "@sveltejs/kit";
import {
  DISCORD_LINK_COOKIE,
  DISCORD_LINK_LIFETIME_MS,
  signDiscordLinkState,
} from "$lib/server/bot/oauth-state";
import { getDiscordOAuthConfig } from "$lib/server/bot/platform-credentials";
import { extractTenantSlug } from "$lib/server/request-host";

/**
 * Get Discord integration configuration. Credentials resolve from the
 * admin-UI platform settings first, then the environment (see
 * getDiscordOAuthConfig).
 */
export const getDiscordConfig = query(async () => {
  const { url, fetch } = getRequestEvent();

  const { applicationId, clientSecret } = await getDiscordOAuthConfig(fetch);
  const baseDomain = process.env.BASE_DOMAIN ?? null;

  return {
    discordApplicationId: applicationId,
    isOauthConfigured: !!applicationId && !!clientSecret,
    baseDomain,
    currentHost: url.host,
  };
});

/** Starts the Discord OAuth2 link flow for the signed-in member; see {@link signDiscordLinkState}. */
export const initiateDiscordLink = command(async () => {
  const { url, fetch, locals, cookies } = getRequestEvent();
  const subjectId = locals.isAuthenticated ? locals.user?.subjectId : undefined;
  if (!subjectId) throw error(401, "Unauthorized");

  const { applicationId: clientId } = await getDiscordOAuthConfig(fetch);
  const baseDomain = process.env.BASE_DOMAIN;
  if (!clientId || !baseDomain) {
    return { error: "Discord OAuth2 is not configured on this server." };
  }

  const slug = extractTenantSlug(url.host, baseDomain);
  if (!slug) {
    return { error: "Could not determine tenant slug from current host." };
  }

  const { state, nonce } = signDiscordLinkState(slug, subjectId);
  cookies.set(DISCORD_LINK_COOKIE, nonce, {
    path: "/",
    httpOnly: true,
    sameSite: "lax",
    maxAge: DISCORD_LINK_LIFETIME_MS / 1000,
  });
  const redirectUri = `https://${baseDomain}/auth/bot/discord/callback`;
  const authorizeUrl = new URL("https://discord.com/api/oauth2/authorize");
  authorizeUrl.searchParams.set("client_id", clientId);
  authorizeUrl.searchParams.set("redirect_uri", redirectUri);
  authorizeUrl.searchParams.set("response_type", "code");
  authorizeUrl.searchParams.set("scope", "identify");
  authorizeUrl.searchParams.set("state", state);
  authorizeUrl.searchParams.set("prompt", "none");

  return { redirectUrl: authorizeUrl.toString() };
});
