import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { isHttpError } from "@sveltejs/kit";
import { errorStatus } from "$lib/forms/submit-error";
import {
	DISCORD_LINK_COOKIE,
	signDiscordLinkClaim,
	signDiscordLinkState,
	verifyDiscordLinkState,
} from "$lib/server/bot/oauth-state";

let event: Record<string, unknown>;

vi.mock("$app/server", () => ({
	getRequestEvent: () => event,
	query: (...args: unknown[]) => args.at(-1),
	command: (...args: unknown[]) => args.at(-1),
}));

const create_pending = vi.fn();
vi.mock("$lib/server/bot/api-client", () => ({
	buildScopedBotApiClient: () => ({ pendingLinks: { create: create_pending } }),
}));

const { confirmDiscordLink, getDiscordLinkClaim } = await import("./finalize.remote");

const discord_user = { id: "123456789012345678", username: "linked-account" };

function browser(cookie: string | undefined, subjectId = "member-a") {
	const jar = new Map(cookie ? [[DISCORD_LINK_COOKIE, cookie]] : []);
	const claim_link = vi.fn().mockResolvedValue({ label: "sleepy", displayName: "Sleepy" });
	event = {
		url: new URL("https://sleepy.nocturne.test/auth/bot/discord/finalize"),
		locals: {
			isAuthenticated: true,
			user: { subjectId },
			apiClient: { chatIdentity: { claimLink: claim_link } },
		},
		cookies: {
			get: (name: string) => jar.get(name),
			delete: (name: string) => jar.delete(name),
		},
		fetch,
	};
	return { jar, claim_link };
}

function startFlow(subjectId = "member-a") {
	const { state, nonce } = signDiscordLinkState("sleepy", subjectId);
	return { nonce, claim: signDiscordLinkClaim(verifyDiscordLinkState(state)!, discord_user) };
}

async function rejectionStatus(call: () => Promise<unknown>): Promise<number | undefined> {
	try {
		await call();
	} catch (rejection) {
		return errorStatus(rejection);
	}
	throw new Error("expected the call to reject");
}

beforeEach(() => {
	vi.stubEnv("INSTANCE_KEY", "a-test-instance-key-of-length");
	vi.stubEnv("BASE_DOMAIN", "nocturne.test");
	create_pending.mockReset().mockResolvedValue({ token: "pending-token" });
});

afterEach(() => {
	vi.unstubAllEnvs();
});

describe("finalizing a Discord link", () => {
	it("names the Discord account to the member who started the flow", async () => {
		const { nonce, claim } = startFlow();
		browser(nonce);

		await expect(getDiscordLinkClaim(claim)).resolves.toEqual({ discordUsername: "linked-account" });
	});

	it("links the account only after the member confirms, then forgets the flow", async () => {
		const { nonce, claim } = startFlow();
		const { jar, claim_link } = browser(nonce);

		await expect(confirmDiscordLink(claim)).resolves.toEqual({ label: "sleepy", displayName: "Sleepy" });

		expect(create_pending).toHaveBeenCalledExactlyOnceWith(
			"discord",
			discord_user.id,
			discord_user.username,
			"sleepy",
			"oauth2-finalize",
		);
		expect(claim_link).toHaveBeenCalledExactlyOnceWith({ token: "pending-token" });
		expect(jar.has(DISCORD_LINK_COOKIE)).toBe(false);
	});

	it.each([
		["holds no link cookie", undefined],
		["holds another flow's link cookie", "a-different-nonce"],
	])("links nothing in a browser that %s", async (_name, cookie) => {
		const { claim } = startFlow();
		const { claim_link } = browser(cookie);

		await expect(getDiscordLinkClaim(claim)).resolves.toBeNull();
		await expect(rejectionStatus(() => confirmDiscordLink(claim))).resolves.toBe(400);
		expect(create_pending).not.toHaveBeenCalled();
		expect(claim_link).not.toHaveBeenCalled();
	});

	it("links nothing for a member other than the one who started the flow", async () => {
		const { nonce, claim } = startFlow("member-a");
		const { claim_link } = browser(nonce, "member-b");

		await expect(rejectionStatus(() => confirmDiscordLink(claim))).resolves.toBe(400);
		expect(claim_link).not.toHaveBeenCalled();
	});

	it.each([
		["the chat account is linked to another member", 409],
		["the API refuses the pending token", 400],
	])("answers %s with the API's status, which reaches the page", async (_name, status) => {
		const { nonce, claim } = startFlow();
		const { claim_link } = browser(nonce);
		claim_link.mockRejectedValue({ status, title: "Refused", detail: "refused" });

		const rejection = await confirmDiscordLink(claim).catch((err: unknown) => err);

		expect(isHttpError(rejection, status)).toBe(true);
	});

	it("passes any other API failure through untouched", async () => {
		const { nonce, claim } = startFlow();
		const { jar, claim_link } = browser(nonce);
		const failure = new Error("upstream down");
		claim_link.mockRejectedValue(failure);

		await expect(confirmDiscordLink(claim)).rejects.toBe(failure);
		expect(jar.has(DISCORD_LINK_COOKIE)).toBe(true);
	});
});
