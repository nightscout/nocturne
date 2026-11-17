import { createHmac, timingSafeEqual, randomBytes } from "node:crypto";
import { z } from "zod";

/**
 * HMAC-signed envelopes for the Discord OAuth2 link flow.
 *
 * The Discord developer portal accepts a single registered redirect URI per application, so SaaS
 * deployments register the apex `/auth/bot/discord/callback` while users start from their tenant
 * subdomain. The state carries the slug to the apex callback; the callback answers with a claim
 * naming the Discord account, which the tenant's finalize page shows for confirmation.
 *
 * Both are bound to the member who started the flow and to their browser: the nonce they carry is
 * also held in {@link DISCORD_LINK_COOKIE}, an HttpOnly cookie on the tenant host, so a claim
 * produced in someone else's browser cannot be redeemed in this one.
 *
 * Wire format: `base64url(json).hexSig`, signed with INSTANCE_KEY (HMAC-SHA256).
 */

export const DISCORD_LINK_COOKIE = "nocturne_discord_link";

export const DISCORD_LINK_LIFETIME_MS = 5 * 60 * 1000;

const LinkStateSchema = z.object({
	purpose: z.literal("discord-link-state"),
	slug: z.string(),
	subjectId: z.string(),
	nonce: z.string(),
	exp: z.number(),
});

const LinkClaimSchema = z.object({
	purpose: z.literal("discord-link-claim"),
	slug: z.string(),
	subjectId: z.string(),
	nonce: z.string(),
	discordUserId: z.string(),
	/** The account's unique handle, or null when Discord reported none in that form. */
	discordUsername: z.string().nullable(),
	exp: z.number(),
});

export type DiscordLinkState = z.infer<typeof LinkStateSchema>;
export type DiscordLinkClaim = z.infer<typeof LinkClaimSchema>;

/** Who is finalizing, and from which browser and tenant. */
export interface DiscordLinkBinding {
	slug: string;
	subjectId: string;
	nonce: string;
}

function getSecret(): string {
	const secret = process.env.INSTANCE_KEY;
	if (!secret || secret.length < 16) {
		throw new Error(
			"INSTANCE_KEY is required for Discord OAuth2 link flow (used as the HMAC secret). " +
				"Set it via Aspire Parameters:instance-key or the INSTANCE_KEY environment variable.",
		);
	}
	return secret;
}

function sign(payload: object): string {
	const payloadB64 = Buffer.from(JSON.stringify(payload), "utf-8").toString("base64url");
	const sig = createHmac("sha256", getSecret()).update(payloadB64).digest("hex");
	return `${payloadB64}.${sig}`;
}

function constantTimeEquals(a: Buffer, b: Buffer): boolean {
	return a.length === b.length && timingSafeEqual(a, b);
}

function verify<T extends { exp: number }>(token: string, schema: z.ZodType<T>): T | null {
	const dot = token.indexOf(".");
	if (dot < 0) return null;
	const payloadB64 = token.slice(0, dot);
	const sigHex = token.slice(dot + 1);
	if (!payloadB64 || !sigHex) return null;

	let expectedHex: string;
	try {
		expectedHex = createHmac("sha256", getSecret()).update(payloadB64).digest("hex");
	} catch {
		return null;
	}
	if (!constantTimeEquals(Buffer.from(sigHex, "hex"), Buffer.from(expectedHex, "hex"))) {
		return null;
	}

	let json: unknown;
	try {
		json = JSON.parse(Buffer.from(payloadB64, "base64url").toString("utf-8"));
	} catch {
		return null;
	}

	const parsed = schema.safeParse(json);
	if (!parsed.success || parsed.data.exp < Date.now()) return null;
	return parsed.data;
}

/** Signs the state for a member starting the flow; `nonce` goes into {@link DISCORD_LINK_COOKIE}. */
export function signDiscordLinkState(
	slug: string,
	subjectId: string,
): { state: string; nonce: string } {
	const nonce = randomBytes(16).toString("hex");
	const state = sign({
		purpose: "discord-link-state",
		slug,
		subjectId,
		nonce,
		exp: Date.now() + DISCORD_LINK_LIFETIME_MS,
	} satisfies DiscordLinkState);
	return { state, nonce };
}

export function verifyDiscordLinkState(state: string): DiscordLinkState | null {
	return verify(state, LinkStateSchema);
}

export function signDiscordLinkClaim(
	state: DiscordLinkState,
	discordUser: { id: string; username: string | null },
): string {
	return sign({
		purpose: "discord-link-claim",
		slug: state.slug,
		subjectId: state.subjectId,
		nonce: state.nonce,
		discordUserId: discordUser.id,
		discordUsername: discordUser.username,
		exp: Date.now() + DISCORD_LINK_LIFETIME_MS,
	} satisfies DiscordLinkClaim);
}

/** The claim, only when it was produced for this member, in this browser, on this tenant. */
export function verifyDiscordLinkClaim(
	claim: string,
	binding: DiscordLinkBinding,
): DiscordLinkClaim | null {
	const payload = verify(claim, LinkClaimSchema);
	if (!payload) return null;
	if (payload.slug.toLowerCase() !== binding.slug.toLowerCase()) return null;
	if (payload.subjectId !== binding.subjectId) return null;
	if (!constantTimeEquals(Buffer.from(binding.nonce), Buffer.from(payload.nonce))) return null;
	return payload;
}
