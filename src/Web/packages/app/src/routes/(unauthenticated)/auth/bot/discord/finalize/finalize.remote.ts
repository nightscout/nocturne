import { command, getRequestEvent, query } from "$app/server";
import { error } from "@sveltejs/kit";
import { z } from "zod";
import { buildScopedBotApiClient } from "#lib/server/bot/api-client.js";
import {
	DISCORD_LINK_COOKIE,
	verifyDiscordLinkClaim,
	type DiscordLinkClaim,
} from "#lib/server/bot/oauth-state.js";
import { extractTenantSlug, getOriginalHost } from "#lib/server/request-host.js";
import { errorStatus } from "#lib/forms/submit-error.js";

function claimForThisRequest(claim: string): DiscordLinkClaim | null {
	const { locals, request, cookies } = getRequestEvent();
	const subjectId = locals.isAuthenticated ? locals.user?.subjectId : undefined;
	const slug = extractTenantSlug(getOriginalHost(request), process.env.BASE_DOMAIN);
	const nonce = cookies.get(DISCORD_LINK_COOKIE);
	if (!subjectId || !slug || !nonce) return null;
	return verifyDiscordLinkClaim(claim, { slug, subjectId, nonce });
}

/** The Discord account the finalize page asks the member to confirm, or null when the claim is not theirs to redeem here. */
export const getDiscordLinkClaim = query(z.string(), async (claim) => {
	const payload = claimForThisRequest(claim);
	return payload ? { discordUsername: payload.discordUsername } : null;
});

export const confirmDiscordLink = command(z.string(), async (claim) => {
	const payload = claimForThisRequest(claim);
	if (!payload) throw error(400, "Invalid or expired Discord link claim.");

	const { locals, cookies, fetch } = getRequestEvent();
	const { token } = await buildScopedBotApiClient(fetch, payload.slug).pendingLinks.create(
		"discord",
		payload.discordUserId,
		payload.discordUsername,
		payload.slug,
		"oauth2-finalize",
	);
	if (!token) throw error(502, "Failed to issue claim token.");

	const link = await locals.apiClient.chatIdentity.claimLink({ token }).catch((err: unknown) => {
		const status = errorStatus(err);
		if (status === 400) throw error(400, "Invalid or expired Discord link claim.");
		if (status === 409) throw error(409, "This Discord account is already linked to another member.");
		throw err;
	});
	cookies.delete(DISCORD_LINK_COOKIE, { path: "/" });
	return { label: link.label ?? "", displayName: link.displayName ?? "" };
});
