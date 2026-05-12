import { createBot, registerAllCommands, AlertDeliveryHandler, type BotOptions, type PlatformCredentials } from "@nocturne/bot";
import type { BotApiClient, AlertDispatchEvent } from "@nocturne/bot";
import { env } from "$env/dynamic/private";
import { createServerApiClient, getApiBaseUrl, getHashedInstanceKey } from "$lib/server/api-client-factory";
import type { PlatformCredentials as ApiPlatformCredentials } from "$api";

type Bot = ReturnType<typeof createBot>;

let botInstance: Bot | null = null;
let botInitPromise: Promise<Bot> | null = null;

export function getBot(): Promise<Bot> {
	if (botInstance) return Promise.resolve(botInstance);
	if (botInitPromise) return botInitPromise;
	botInitPromise = initBot().then((bot) => {
		botInstance = bot;
		botInitPromise = null;
		return bot;
	});
	return botInitPromise;
}

async function fetchDbCredentials(): Promise<Map<string, ApiPlatformCredentials> | null> {
	const apiBaseUrl = getApiBaseUrl();
	if (!apiBaseUrl) return null;
	try {
		const client = createServerApiClient(apiBaseUrl, fetch, {
			hashedInstanceKey: getHashedInstanceKey(),
			extraHeaders: { "X-Forwarded-Proto": "https" },
		});
		const all = await client.platformSettings.getAllDecrypted();
		if (!all) return null;
		return new Map(all.map((c) => [c.category!, c]));
	} catch (err) {
		console.warn("[bot] Failed to fetch platform settings from API, falling back to env vars:", err);
		return null;
	}
}

type PlatformConfigBuilder = {
	fromDb: (fields: Record<string, string>) => PlatformCredentials;
	fromEnv: () => boolean;
};

const platformBuilders: Record<string, PlatformConfigBuilder> = {
	discord: {
		fromDb: (fields) => ({
			enabled: true,
			botToken: fields["botToken"],
			publicKey: fields["publicKey"],
			applicationId: fields["applicationId"],
		}),
		fromEnv: () => !!env.DISCORD_BOT_TOKEN,
	},
	slack: {
		fromDb: (fields) => ({
			enabled: true,
			botToken: fields["botToken"],
			signingSecret: fields["signingSecret"],
		}),
		fromEnv: () => !!(env.SLACK_BOT_TOKEN && env.SLACK_SIGNING_SECRET),
	},
	telegram: {
		fromDb: (fields) => ({
			enabled: true,
			botToken: fields["botToken"],
		}),
		fromEnv: () => !!env.TELEGRAM_BOT_TOKEN,
	},
	whatsapp: {
		fromDb: (fields) => ({
			enabled: true,
			accessToken: fields["accessToken"],
			appSecret: fields["appSecret"],
			phoneNumberId: fields["phoneNumberId"],
			verifyToken: fields["verifyToken"],
		}),
		fromEnv: () => false, // WhatsApp has no env var fallback
	},
};

function buildPlatformConfig(
	category: string,
	dbCredentials: Map<string, ApiPlatformCredentials> | null,
): PlatformCredentials | boolean {
	const builder = platformBuilders[category];
	if (!builder) return false;

	const db = dbCredentials?.get(category);
	if (db !== undefined) {
		if (!db.enabled) return false;
		return builder.fromDb(db.fields ?? {});
	}
	return builder.fromEnv();
}

async function initBot(): Promise<Bot> {
	const dbCredentials = await fetchDbCredentials();

	const options: BotOptions = {
		platforms: {
			discord: buildPlatformConfig("discord", dbCredentials),
			slack: buildPlatformConfig("slack", dbCredentials),
			telegram: buildPlatformConfig("telegram", dbCredentials),
			whatsapp: buildPlatformConfig("whatsapp", dbCredentials),
		},
		// The bot adapter expects a postgresql:// URL, not the .NET-style
		// ConnectionStrings__nocturne-postgres value (which is
		// Host=...;Port=...;Database=... key/value format and causes the
		// pg client to resolve literal strings like "base" as hostnames).
		// Aspire injects NOCTURNE_POSTGRES_URI pointing at the
		// nocturne_web role — a least-privileged role that owns only its
		// own chat_state_* tables and cannot touch tenant-scoped tables.
		// Fall back to DATABASE_URL for non-Aspire deployments.
		postgresUrl:
			process.env.NOCTURNE_POSTGRES_URI ??
			process.env.DATABASE_URL ??
			"",
	};

	const bot = createBot(options);

	const baseDomain = process.env.BASE_DOMAIN;
	if (!baseDomain) {
		throw new Error(
			"BASE_DOMAIN is required for bot /connect link generation. " +
				"Set it via Aspire AppHost parameters or your .env file (e.g. localhost:1612 for dev).",
		);
	}
	registerAllCommands(bot, baseDomain);

	const enabledPlatforms = (Object.entries(options.platforms ?? {}) as [string, PlatformCredentials | boolean][])
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
