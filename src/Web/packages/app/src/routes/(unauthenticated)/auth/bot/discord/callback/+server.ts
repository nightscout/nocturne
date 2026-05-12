import type { RequestHandler } from "./$types";
import { error, redirect } from "@sveltejs/kit";
import {
	createServerApiClient,
	getApiBaseUrl,
	getHashedInstanceKey,
} from "$lib/server/api-client-factory";
import { verifyOAuthLinkState } from "$lib/server/bot/oauth-state";

/**
 * Apex-hosted Discord OAuth2 callback.
 *
 * Flow:
 *   1. User clicks "Link Discord account" on Settings → Integrations → Discord
 *      (a tenant subdomain page).
 *   2. That page redirects to Discord's authorize URL with an HMAC-signed
 *      state parameter that carries the tenant slug.
 *   3. Discord redirects back here (apex) with `code` + `state`.
 *   4. We verify the HMAC, exchange the code for an access token, fetch the
 *      Discord user id, issue a short-lived pending-link token bound to
 *      (discord, userId, slug, "oauth2-finalize"), and redirect to the
 *      tenant-scoped finalize hop.
 *   5. The finalize hop consumes the token and creates the directory link.
 *
 * This route lives on the apex and bypasses tenant resolution via the
 * /auth/* allowlist in siteSecurityHandle.
 */
export const GET: RequestHandler = async ({ url, fetch }) => {
	const code = url.searchParams.get("code");
	const state = url.searchParams.get("state");
	if (!code || !state) {
		throw error(400, "Missing code or state parameter.");
	}

	const payload = verifyOAuthLinkState(state);
	if (!payload) {
		throw error(400, "Invalid or expired state.");
	}

	const clientId = process.env.DISCORD_APPLICATION_ID;
	const clientSecret = process.env.DISCORD_CLIENT_SECRET;
	const baseDomain = process.env.BASE_DOMAIN;
	if (!clientId || !clientSecret || !baseDomain) {
		throw error(
			500,
			"Discord OAuth2 not configured. Set DISCORD_APPLICATION_ID, DISCORD_CLIENT_SECRET, and BASE_DOMAIN.",
		);
	}

	const redirectUri = `https://${baseDomain}/auth/bot/discord/callback`;

	// Exchange authorization code for an access token
	const tokenResponse = await fetch("https://discord.com/api/oauth2/token", {
		method: "POST",
		headers: { "Content-Type": "application/x-www-form-urlencoded" },
		body: new URLSearchParams({
			client_id: clientId,
			client_secret: clientSecret,
			grant_type: "authorization_code",
			code,
			redirect_uri: redirectUri,
		}),
	});
	if (!tokenResponse.ok) {
		console.error(
			"Discord token exchange failed:",
			tokenResponse.status,
			await tokenResponse.text().catch(() => ""),
		);
		throw error(502, "Discord token exchange failed.");
	}
	const tokenJson = (await tokenResponse.json()) as { access_token?: string };
	const accessToken = tokenJson.access_token;
	if (!accessToken) {
		throw error(502, "Discord did not return an access token.");
	}

	// Fetch the Discord user profile (identify scope)
	const userResponse = await fetch("https://discord.com/api/users/@me", {
		headers: { Authorization: `Bearer ${accessToken}` },
	});
	if (!userResponse.ok) {
		throw error(502, "Failed to fetch Discord user profile.");
	}
	const discordUser = (await userResponse.json()) as { id?: string };
	if (!discordUser.id) {
		throw error(502, "Discord user profile missing id.");
	}

	// Issue a short-lived claim token bound to this Discord user + tenant slug.
	// The finalize hop on the tenant subdomain will consume it.
	const apiBaseUrl = getApiBaseUrl();
	if (!apiBaseUrl) {
		throw error(500, "API base URL not configured.");
	}
	const apiClient = createServerApiClient(apiBaseUrl, fetch, {
		hashedInstanceKey: getHashedInstanceKey(),
		extraHeaders: { "X-Forwarded-Proto": "https" },
	});

	const pendingResponse = await apiClient.chatIdentityDirectory.createPending({
		platform: "discord",
		platformUserId: discordUser.id,
		tenantSlug: payload.slug,
		source: "oauth2-finalize",
	});
	const claimToken = pendingResponse.token;
	if (!claimToken) {
		throw error(502, "Failed to issue claim token.");
	}

	throw redirect(
		302,
		`https://${payload.slug}.${baseDomain}/auth/bot/discord/finalize?token=${claimToken}`,
	);
};
