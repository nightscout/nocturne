import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { errorStatus } from "#lib/forms/submit-error.js";
import { DISCORD_LINK_COOKIE, verifyDiscordLinkState } from "#lib/server/bot/oauth-state.js";

let event: Record<string, unknown>;

vi.mock("$app/server", () => ({
	getRequestEvent: () => event,
	query: (...args: unknown[]) => args.at(-1),
	command: (...args: unknown[]) => args.at(-1),
}));

vi.mock("#lib/server/bot/platform-credentials.js", () => ({
	getDiscordOAuthConfig: async () => ({ applicationId: "000000000000000000", clientSecret: "secret" }),
}));

const { initiateDiscordLink } = await import("./discord.remote");

function request(locals: Record<string, unknown>) {
	const set = vi.fn();
	event = {
		url: new URL("https://sleepy.nocturne.test/settings/integrations/discord"),
		locals,
		cookies: { set },
		fetch,
	};
	return set;
}

beforeEach(() => {
	vi.stubEnv("INSTANCE_KEY", "a-test-instance-key-of-length");
	vi.stubEnv("BASE_DOMAIN", "nocturne.test");
});

afterEach(() => {
	vi.unstubAllEnvs();
});

describe("initiateDiscordLink", () => {
	it("refuses a visitor who is not signed in", async () => {
		const set_cookie = request({ isAuthenticated: false, user: null });

		await expect(
			initiateDiscordLink().then(
				() => undefined,
				(rejection: unknown) => errorStatus(rejection),
			),
		).resolves.toBe(401);
		expect(set_cookie).not.toHaveBeenCalled();
	});

	it("binds the state to the signed-in member and to this browser's link cookie", async () => {
		const set_cookie = request({ isAuthenticated: true, user: { subjectId: "member-a" } });

		const result = await initiateDiscordLink();
		const state = new URL((result as { redirectUrl: string }).redirectUrl).searchParams.get("state")!;
		const payload = verifyDiscordLinkState(state);

		expect(payload).toMatchObject({ slug: "sleepy", subjectId: "member-a" });
		expect(set_cookie).toHaveBeenCalledExactlyOnceWith(
			DISCORD_LINK_COOKIE,
			payload!.nonce,
			expect.objectContaining({ httpOnly: true, sameSite: "lax", path: "/" }),
		);
	});
});
