import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AlertDispatchEvent, BotApiClient } from "@nocturne/bot";

interface MemoryState {
	connect(): Promise<void>;
	set(key: string, value: unknown): Promise<void>;
}

const held = vi.hoisted(() => ({ state: null as MemoryState | null, bot: null as Promise<unknown> | null }));

// The bot's entry point and its state adapter load from the bot's own sources and dependencies,
// which need no build and no Postgres.
const paths = await vi.hoisted(async () => {
	const { createRequire } = await import("node:module");
	const { dirname, join } = await import("node:path");
	const { pathToFileURL } = await import("node:url");
	const botRoot = dirname(createRequire(import.meta.url).resolve("@nocturne/bot/package.json"));
	const entry = (name: string) => join(botRoot, "node_modules", name, "dist/index.js");
	return {
		bot: pathToFileURL(join(botRoot, "src/index.ts")).href,
		pg: entry("@chat-adapter/state-pg"),
		memory: pathToFileURL(entry("@chat-adapter/state-memory")).href,
		resend: entry("@resend/chat-sdk-adapter"),
	};
});

// Loaded once: each test resets the app's modules to get a bot that is not yet initialised, and the
// bot's sources are slow to load.
vi.mock("@nocturne/bot", () => (held.bot ??= import(/* @vite-ignore */ paths.bot)));
// Resend is not under test, and its adapter needs a peer that a workspace install may not link here.
vi.mock(paths.resend, () => ({ createResendAdapter: () => ({}) }));
vi.mock(paths.pg, () => ({ createPostgresState: () => held.state }));
vi.mock("./platform-credentials", () => ({
	fetchDecryptedPlatformCredentials: async () => [
		{
			category: "whatsapp",
			enabled: true,
			fields: { accessToken: "not-a-token", appSecret: "not-a-secret", phoneNumberId: "1000", verifyToken: "not-a-token" },
		},
	],
}));
vi.mock("./state-url", () => ({ botPostgresUrl: () => "postgres://unused" }));
vi.mock("#lib/server/api-client-factory.js", () => ({ getApiBaseUrl: () => null, createServerApiClient: vi.fn() }));
vi.mock("#lib/server/instance-key.js", () => ({ getHashedInstanceKey: () => "hashed" }));

const THREAD_NUMBER = "61400000000";
const CURRENT_NUMBER = "61400000111";

function alert(): AlertDispatchEvent {
	return {
		deliveryId: "delivery-1",
		channelType: "whatsapp_dm",
		destination: THREAD_NUMBER,
		tenantSlug: "acme",
		payload: {
			tenantId: "11111111-1111-1111-1111-111111111111",
			excursionId: "33333333-3333-3333-3333-333333333333",
			ruleName: "Urgent low",
			subjectName: "Alex",
			glucoseValue: 54,
			readingTimestamp: "2026-11-26T03:00:00Z",
			severity: "critical",
		},
	} as AlertDispatchEvent;
}

describe("the bot an alert is delivered by", { timeout: 60_000 }, () => {
	let sent: { to?: string }[];
	let api: BotApiClient;
	const baseDomain = process.env.BASE_DOMAIN;

	beforeEach(async () => {
		vi.resetModules();
		process.env.BASE_DOMAIN = "nocturne.test";
		const { createMemoryState } = await import(/* @vite-ignore */ paths.memory);
		held.state = createMemoryState();
		await held.state!.connect();
		// The user changed number: Meta reported it, so the route holds the new one and the thread the old one.
		await held.state!.set(`whatsapp:identity:route:1000:${THREAD_NUMBER}`, { phone: CURRENT_NUMBER });

		sent = [];
		vi.stubGlobal(
			"fetch",
			vi.fn(async (_url: string, init?: { body?: string }) => {
				sent.push(JSON.parse(init?.body ?? "{}"));
				return new Response(JSON.stringify({ messages: [{ id: "wamid.out" }] }));
			}),
		);
		api = { alerts: { markDelivered: vi.fn().mockResolvedValue(undefined), markFailed: vi.fn() } } as unknown as BotApiClient;
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		if (baseDomain === undefined) delete process.env.BASE_DOMAIN;
		else process.env.BASE_DOMAIN = baseDomain;
	});

	it("sends the first alert after a restart, before any webhook, to the number the route holds now", async () => {
		const { handleBotDispatch } = await import("./index");

		await handleBotDispatch(alert(), api);

		expect(sent).toHaveLength(1);
		expect(sent[0].to).toBe(`+${CURRENT_NUMBER}`);
	});

	it("initialises once when the first calls arrive together, and hands every caller the same bot", async () => {
		const { getBot } = await import("./index");
		const connect = vi.spyOn(held.state!, "connect");

		const bots = await Promise.all([getBot(), getBot(), getBot()]);

		expect(new Set(bots).size).toBe(1);
		expect(connect).toHaveBeenCalledTimes(1);
	});

	it("tries again after an initialisation that failed, rather than hand out the failure for ever", async () => {
		const { getBot } = await import("./index");
		vi.spyOn(held.state!, "connect").mockRejectedValueOnce(new Error("connect ECONNREFUSED"));

		await expect(getBot()).rejects.toThrow("ECONNREFUSED");
		await expect(getBot()).resolves.toBeDefined();
	});
});
