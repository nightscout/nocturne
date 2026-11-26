import type { RequestHandler } from "./$types";
import { handleBotWebhook } from "#lib/server/bot/webhook.js";

export const POST: RequestHandler = (event) => handleBotWebhook("slack", event);
