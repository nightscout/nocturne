import { createHmac, timingSafeEqual, randomBytes } from "node:crypto";
import { z } from "zod";

/**
 * HMAC-signed state token for the Discord OAuth2 link flow.
 *
 * Purpose: the Discord developer portal only accepts a single registered
 * OAuth2 redirect URI per application. For SaaS deployments we register the
 * apex `/auth/bot/discord/callback`. Users click "Link Discord" from their
 * tenant subdomain, so the state parameter must carry the originating slug
 * back to the apex callback in a tamper-proof way.
 *
 * Wire format: `base64url(json).hexSig`.
 * Payload: `{ slug, nonce, exp }` (exp is a unix-ms deadline).
 * Signature: HMAC-SHA256 over the base64url payload using INSTANCE_KEY.
 *
 * INSTANCE_KEY is reused here rather than a separate BOT_LINK_HMAC_SECRET
 * because both secrets have the same threat model (tenant-wide shared
 * secret held by the web process) and the OAuth state is short-lived
 * (5 minutes), so even if INSTANCE_KEY is rotated the user impact is
 * dominated by JWT invalidation, not state token invalidation.
 */

const OAuthLinkStatePayloadSchema = z.object({
	/** Tenant subdomain slug the user came from — the callback redirects back here. */
	slug: z.string(),
	/** Random nonce to prevent replay. */
	nonce: z.string(),
	/** Expiration timestamp in unix milliseconds. */
	exp: z.number(),
});

export type OAuthLinkStatePayload = z.infer<typeof OAuthLinkStatePayloadSchema>;

const STATE_LIFETIME_MS = 5 * 60 * 1000; // 5 minutes

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

export function signOAuthLinkState(slug: string): string {
	const payload: OAuthLinkStatePayload = {
		slug,
		nonce: randomBytes(16).toString("hex"),
		exp: Date.now() + STATE_LIFETIME_MS,
	};
	const payloadB64 = Buffer.from(JSON.stringify(payload), "utf-8").toString("base64url");
	const sig = createHmac("sha256", getSecret()).update(payloadB64).digest("hex");
	return `${payloadB64}.${sig}`;
}

export function verifyOAuthLinkState(state: string): OAuthLinkStatePayload | null {
	const dot = state.indexOf(".");
	if (dot < 0) return null;
	const payloadB64 = state.slice(0, dot);
	const sigHex = state.slice(dot + 1);

	if (!payloadB64 || !sigHex) return null;

	let expectedHex: string;
	try {
		expectedHex = createHmac("sha256", getSecret()).update(payloadB64).digest("hex");
	} catch {
		return null;
	}

	const actual = Buffer.from(sigHex, "hex");
	const expected = Buffer.from(expectedHex, "hex");
	if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) {
		return null;
	}

	let json: unknown;
	try {
		json = JSON.parse(Buffer.from(payloadB64, "base64url").toString("utf-8"));
	} catch {
		return null;
	}

	const parsed = OAuthLinkStatePayloadSchema.safeParse(json);
	if (!parsed.success) return null;
	if (parsed.data.exp < Date.now()) return null;

	return parsed.data;
}
