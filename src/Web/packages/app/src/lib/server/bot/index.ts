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
			// ConnectionStrings__nocturne-postgres value Aspire injects (which
			// is Host=...;Port=...;Database=... key/value format and causes
			// the pg client to resolve literal strings like "base" as hostnames).
			// Aspire also injects NOCTURNE_POSTGRES_URI in standard URL form,
			// which is what we want. Fall back to DATABASE_URL for non-Aspire
			// deployments.
			postgresUrl:
				process.env.NOCTURNE_POSTGRES_URI ??
				process.env.DATABASE_URL ??
				"",
		};
		botInstance = createBot(options);
		// No dedicated public-URL env var exists yet; fall back to SvelteKit's
		// ORIGIN (set in adapter-node configs). If empty, the /connect command
		// will produce a relative authorize link.
		registerAllCommands(botInstance, env.ORIGIN ?? "");
	}
	return botInstance;
}

export async function handleBotDispatch(event: AlertDispatchEvent, api: BotApiClient): Promise<void> {
	const bot = getBot();
	const handler = new AlertDeliveryHandler(bot, api);
	await handler.deliver(event);
}
