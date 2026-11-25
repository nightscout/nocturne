import type { RequestHandler } from "./$types";
import { handleBotWebhook } from "#lib/server/bot/webhook.js";

/** Meta's callback URL verification: the adapter echoes `hub.challenge` when the verify token matches. */
export const GET: RequestHandler = (event) => handleBotWebhook("whatsapp", event);

export const POST: RequestHandler = (event) => handleBotWebhook("whatsapp", event);
