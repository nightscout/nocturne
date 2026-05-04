/**
 * Remote functions for the Discord integration settings page.
 *
 * Only contains server-side logic that needs process.env or crypto.
 * For CRUD operations on chat identity links, use
 * $lib/api/generated/chatIdentities.generated.remote.ts.
 */
import { getRequestEvent, query, command } from "$app/server";
import { signOAuthLinkState } from "$lib/server/bot/oauth-state";

/**
 * Get Discord integration configuration from server environment.
 */
export const getDiscordConfig = query(async () => {
  const { url } = getRequestEvent();

  const discordApplicationId = process.env.DISCORD_APPLICATION_ID ?? null;
  const isOauthConfigured =
    !!process.env.DISCORD_APPLICATION_ID && !!process.env.DISCORD_CLIENT_SECRET;
  const baseDomain = process.env.PUBLIC_BASE_DOMAIN ?? null;

  return {
    discordApplicationId,
    isOauthConfigured,
    baseDomain,
    currentHost: url.host,
  };
});

/**
 * Initiate the Discord OAuth2 link flow.
 * Needs server-only crypto (HMAC signing) and process.env access.
 */
export const initiateDiscordLink = command(async () => {
  const { url } = getRequestEvent();

  const clientId = process.env.DISCORD_APPLICATION_ID;
  const baseDomain = process.env.PUBLIC_BASE_DOMAIN;
  if (!clientId || !baseDomain) {
    return { error: "Discord OAuth2 is not configured on this server." };
  }

  // Extract tenant slug from host
  const baseHost = baseDomain.split(":")[0] ?? baseDomain;
  const currentHost = url.host.split(":")[0] ?? url.host;
  let slug: string | null = null;
  if (currentHost.endsWith(`.${baseHost}`)) {
    slug = currentHost.slice(0, currentHost.length - baseHost.length - 1) || null;
  }

  if (!slug) {
    return { error: "Could not determine tenant slug from current host." };
  }

  const state = signOAuthLinkState(slug);
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
