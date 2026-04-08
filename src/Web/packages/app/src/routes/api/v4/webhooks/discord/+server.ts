import type { RequestHandler } from "./$types";
import { getBot } from "$lib/server/bot";
import { runWithApi } from "@nocturne/bot";
import { buildBotApiClient } from "$lib/server/bot/api-client";

export const POST: RequestHandler = async ({ request, locals }) => {
	const bot = getBot();
	const botApiClient = buildBotApiClient(locals.apiClient);

	// IMPORTANT: The Discord adapter auto-defers the interaction response and
	// runs slash/action handlers detached from this request. Node ALS propagates
	// through async-task inheritance, so handlers called inside this runWithApi
	// scope will see botApiClient via getApi() even after we return.
	//
	// This relies on adapter-node keeping the event loop alive after the response
	// is sent. If Nocturne ever moves to a serverless SvelteKit adapter, the
	// detached tasks will be killed and this needs a waitUntil-equivalent.
	return runWithApi(botApiClient, () => bot.webhooks.discord(request));
};
