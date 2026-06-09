/**
 * Remote functions for the Discord integration settings page.
 *
 * Only contains server-side logic that needs process.env or crypto.
 * For CRUD operations on chat identity links, use
 * $lib/api/generated/chatIdentities.generated.remote.ts.
 */
import { getRequestEvent, query, command } from "$app/server";
import { signOAuthLinkState } from "$lib/server/bot/oauth-state";
import { getDiscordOAuthConfig } from "$lib/server/bot/platform-credentials";

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

/**
 * Initiate the Discord OAuth2 link flow.
 * Needs server-only crypto (HMAC signing) and process.env access.
 */
export const initiateDiscordLink = command(async () => {
  const { url, fetch } = getRequestEvent();

  const { applicationId: clientId } = await getDiscordOAuthConfig(fetch);
  const baseDomain = process.env.BASE_DOMAIN;
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
