import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
	DISCORD_LINK_LIFETIME_MS,
	signDiscordLinkClaim,
	signDiscordLinkState,
	verifyDiscordLinkClaim,
	verifyDiscordLinkState,
} from "./oauth-state";

const discord_user = { id: "123456789012345678", username: "sleepy" };

beforeEach(() => {
	vi.stubEnv("INSTANCE_KEY", "a-test-instance-key-of-length");
});

afterEach(() => {
	vi.unstubAllEnvs();
	vi.useRealTimers();
});

function startedBy(subjectId: string, slug = "sleepy") {
	const { state, nonce } = signDiscordLinkState(slug, subjectId);
	const claim = signDiscordLinkClaim(verifyDiscordLinkState(state)!, discord_user);
	return { state, nonce, claim };
}

describe("the Discord link claim", () => {
	it("is redeemable by the member who started it, in the browser holding its nonce", () => {
		const { nonce, claim } = startedBy("member-a");

		expect(
			verifyDiscordLinkClaim(claim, { slug: "sleepy", subjectId: "member-a", nonce }),
		).toMatchObject({ discordUserId: discord_user.id, discordUsername: "sleepy" });
	});

	it("is not redeemable in a browser that holds another flow's nonce", () => {
		const { claim } = startedBy("member-a");
		const { nonce: other_nonce } = startedBy("member-a");

		expect(
			verifyDiscordLinkClaim(claim, { slug: "sleepy", subjectId: "member-a", nonce: other_nonce }),
		).toBeNull();
	});

	it("is not redeemable by a different member", () => {
		const { nonce, claim } = startedBy("member-a");

		expect(verifyDiscordLinkClaim(claim, { slug: "sleepy", subjectId: "member-b", nonce })).toBeNull();
	});

	it("is not redeemable on a different tenant", () => {
		const { nonce, claim } = startedBy("member-a", "other");

		expect(verifyDiscordLinkClaim(claim, { slug: "sleepy", subjectId: "member-a", nonce })).toBeNull();
	});

	it("does not accept the state in its place", () => {
		const { state, nonce } = startedBy("member-a");

		expect(verifyDiscordLinkClaim(state, { slug: "sleepy", subjectId: "member-a", nonce })).toBeNull();
	});

	it("does not accept a claim whose payload was altered", () => {
		const { nonce, claim } = startedBy("member-a");
		const [payload, sig] = claim.split(".");
		const altered = JSON.parse(Buffer.from(payload!, "base64url").toString("utf-8"));
		altered.discordUserId = "999";
		const forged = `${Buffer.from(JSON.stringify(altered)).toString("base64url")}.${sig}`;

		expect(verifyDiscordLinkClaim(forged, { slug: "sleepy", subjectId: "member-a", nonce })).toBeNull();
	});

	it("expires", () => {
		vi.useFakeTimers();
		const { nonce, claim } = startedBy("member-a");
		vi.advanceTimersByTime(DISCORD_LINK_LIFETIME_MS + 1);

		expect(verifyDiscordLinkClaim(claim, { slug: "sleepy", subjectId: "member-a", nonce })).toBeNull();
	});
});
