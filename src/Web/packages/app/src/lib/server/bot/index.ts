import { createBot, registerAllCommands, AlertDeliveryHandler, type BotOptions } from "@nocturne/bot";
import type { BotApiClient, AlertDispatchEvent } from "@nocturne/bot";
import { env } from "$env/dynamic/private";

type Bot = ReturnType<typeof createBot>;

let botInstance: Bot | null = null;

export function getBot(): Bot {
	if (!botInstance) {
		const options: BotOptions = {
			platforms: {
				discord: !!env.DISCORD_BOT_TOKEN,
				slack: !!env.SLACK_BOT_TOKEN && !!env.SLACK_SIGNING_SECRET,
				telegram: !!env.TELEGRAM_BOT_TOKEN,
				whatsapp: !!env.WHATSAPP_ACCESS_TOKEN,
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
		botInstance = createBot(options);
		const baseDomain = process.env.PUBLIC_BASE_DOMAIN;
		if (!baseDomain) {
			throw new Error(
				"PUBLIC_BASE_DOMAIN is required for bot /connect link generation. " +
					"Set it via Aspire AppHost parameters or your .env file (e.g. localhost:1612 for dev).",
			);
		}
		registerAllCommands(botInstance, baseDomain);
	}
	return botInstance;
}

export async function handleBotDispatch(event: AlertDispatchEvent, api: BotApiClient): Promise<void> {
	const bot = getBot();
	const handler = new AlertDeliveryHandler(bot, api);
	await handler.deliver(event);
}
