import type { RequestHandler } from "./$types";
import { getBot } from "#lib/server/bot/index.js";

export const POST: RequestHandler = async ({ request }) => {
	const bot = await getBot();
	return bot.webhooks.whatsapp(request);
};
