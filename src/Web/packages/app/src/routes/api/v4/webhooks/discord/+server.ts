import type { RequestHandler } from "./$types";
import { getBot } from "$lib/server/bot";
import { runWithContext, type BotRequestContext } from "@nocturne/bot";
import { buildUnscopedBotApiClient, buildScopedBotApiClient } from "$lib/server/bot/api-client";
import { getEffectiveHost, getOriginalProto } from "$lib/server/request-host";

export const POST: RequestHandler = async ({ request, cookies, fetch }) => {
	const bot = await getBot();

	// Build the unscoped (apex) BotApiClient + a scoped factory closure that
	// the bot package's requireLink helper will call once it resolves the
	// tenant from the directory lookup. See plan Task 4.3 (α decision):
	// tenant resolution is late-bound inside requireLink, not here at the
	// route boundary.
	//
	// The bot is a trusted service, so it uses an explicit instance-key client
	// (never locals.apiClient, which carries only the end user's credentials).
	const extraHeaders: Record<string, string> = {
		"X-Forwarded-Proto": getOriginalProto(request),
	};
	const host = getEffectiveHost(request, cookies);
	if (host) extraHeaders["X-Forwarded-Host"] = host;

	const context: BotRequestContext = {
		unscopedApi: buildUnscopedBotApiClient(fetch, extraHeaders),
		scopedApiFactory: (tenantSlug: string) => buildScopedBotApiClient(fetch, tenantSlug),
		resolvedTenantSlug: null,
		resolvedLink: null,
	};

	// IMPORTANT: The Discord adapter auto-defers the interaction response and
	// runs slash/action handlers detached from this request. Node ALS propagates
	// through async-task inheritance, so handlers called inside this runWithContext
	// scope will see the BotRequestContext via getUnscopedApi()/requireLink()/getApi()
	// even after we return.
	//
	// This relies on adapter-node keeping the event loop alive after the response
	// is sent. If Nocturne ever moves to a serverless SvelteKit adapter, the
	// detached tasks will be killed and this needs a waitUntil-equivalent.
	return runWithContext(context, () => bot.webhooks.discord(request));
};
