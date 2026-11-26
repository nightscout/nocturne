import type { RequestHandler } from "./$types";
import { handleBotWebhook } from "#lib/server/bot/webhook.js";

/** Every chat platform's webhook; the platform's module says which methods it takes (Meta verifies with a GET). */
const handle: RequestHandler = (event) => handleBotWebhook(event.params.platform, event);

export const GET = handle;
export const POST = handle;
