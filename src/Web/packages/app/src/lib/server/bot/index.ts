import {
	createBot,
	registerAllCommands,
	resolvePlatforms,
	AlertDeliveryHandler,
	type BotOptions,
} from "@nocturne/bot";
import type { BotApiClient, AlertDispatchEvent } from "@nocturne/bot";
import { createServerApiClient, getApiBaseUrl } from "#lib/server/api-client-factory.js";
import { getHashedInstanceKey } from "#lib/server/instance-key.js";
import { fetchDecryptedPlatformCredentials } from "./platform-credentials";
import { botPostgresUrl } from "./state-url";

type Bot = ReturnType<typeof createBot>;

let botInstance: Bot | null = null;
let botInitPromise: Promise<Bot> | null = null;

export function getBot(): Promise<Bot> {
	if (botInstance) return Promise.resolve(botInstance);
	if (botInitPromise) return botInitPromise;
	botInitPromise = initBot().then(
		(bot) => {
			botInstance = bot;
			botInitPromise = null;
			return bot;
		},
		(err: unknown) => {
			botInitPromise = null;
			throw err;
		},
	);
	return botInitPromise;
}

async function initBot(): Promise<Bot> {
	const options: BotOptions = {
		platforms: resolvePlatforms(await fetchDecryptedPlatformCredentials(fetch)),
		postgresUrl: botPostgresUrl(),
	};

	const baseDomain = process.env.BASE_DOMAIN;
	if (!baseDomain) {
		throw new Error(
			"BASE_DOMAIN is required for bot /connect link generation. " +
				"Set it via Aspire AppHost parameters or your .env file (e.g. localhost:1612 for dev).",
		);
	}

	const bot = createBot(options);
	registerAllCommands(bot, baseDomain);

	try {
		// An adapter holds no chat until the bot initialises, and a WhatsApp send without one skips
		// the stored route and goes to the number on the thread.
		await bot.initialize();
	} catch (err) {
		await bot.shutdown().catch(() => {});
		throw err;
	}

	const enabledPlatforms = Object.entries(options.platforms ?? {})
		.filter(([, val]) => val !== false && val !== undefined)
		.map(([name]) => name);

	if (enabledPlatforms.length > 0) {
		const apiBaseUrl = getApiBaseUrl();
		if (apiBaseUrl) {
			const heartbeatClient = createServerApiClient(apiBaseUrl, fetch, {
				hashedInstanceKey: getHashedInstanceKey(),
				extraHeaders: { "X-Forwarded-Proto": "https" },
			});

			const sendHeartbeat = () => {
				heartbeatClient.system
					.heartbeat({ platforms: enabledPlatforms, service: "bot" })
					.catch((err: unknown) => {
						console.warn("[bot] heartbeat failed:", err);
					});
			};

			sendHeartbeat();
			setInterval(sendHeartbeat, 60_000);
		}
	}

	return bot;
}

export async function handleBotDispatch(event: AlertDispatchEvent, api: BotApiClient): Promise<void> {
	const bot = await getBot();
	const handler = new AlertDeliveryHandler(bot, api);
	await handler.deliver(event);
}
