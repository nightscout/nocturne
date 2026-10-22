import type { RequestHandler } from "./$types";
import { error, redirect } from "@sveltejs/kit";
import { z } from "zod";
import { platformHandle } from "@nocturne/bot";
import { signDiscordLinkClaim, verifyDiscordLinkState } from "$lib/server/bot/oauth-state";
import { getDiscordOAuthConfig } from "$lib/server/bot/platform-credentials";

const DiscordTokenSchema = z.object({ access_token: z.string().optional() });
const DiscordUserSchema = z.object({
	id: z.string().optional(),
	username: z.string().optional(),
});

/**
 * Apex-hosted Discord OAuth2 callback. Exchanges the code for the Discord user and hands a signed
 * claim naming that account to the tenant's finalize page, which asks the member to confirm it.
 * Lives on the apex and bypasses tenant resolution via the /auth/* allowlist in siteSecurityHandle.
 */
export const GET: RequestHandler = async ({ url, fetch }) => {
	const code = url.searchParams.get("code");
	const state = url.searchParams.get("state");
	if (!code || !state) {
		throw error(400, "Missing code or state parameter.");
	}

	const payload = verifyDiscordLinkState(state);
	if (!payload) {
		throw error(400, "Invalid or expired state.");
	}

	const { applicationId: clientId, clientSecret } = await getDiscordOAuthConfig(fetch);
	const baseDomain = process.env.BASE_DOMAIN;
	if (!clientId || !clientSecret || !baseDomain) {
		throw error(
			500,
			"Discord OAuth2 not configured. Set Discord credentials in Settings > Admin > Integrations (or DISCORD_APPLICATION_ID / DISCORD_CLIENT_SECRET) and set BASE_DOMAIN.",
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
	const accessToken = DiscordTokenSchema.safeParse(await tokenResponse.json()).data?.access_token;
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
	const discordUser = DiscordUserSchema.safeParse(await userResponse.json()).data;
	if (!discordUser?.id) {
		throw error(502, "Discord user profile missing id.");
	}

	const claim = signDiscordLinkClaim(payload, {
		id: discordUser.id,
		username: platformHandle("discord", discordUser.username),
	});
	throw redirect(
		302,
		`https://${payload.slug}.${baseDomain}/auth/bot/discord/finalize?claim=${encodeURIComponent(claim)}`,
	);
};
