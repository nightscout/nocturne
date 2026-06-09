import type { RequestHandler } from "./$types";
import { handleBotDispatch } from "$lib/server/bot";
import { buildUnscopedBotApiClient } from "$lib/server/bot/api-client";
import { getEffectiveHost, getOriginalProto } from "$lib/server/request-host";
import type { AlertDispatchEvent } from "@nocturne/bot";

export const POST: RequestHandler = async ({ request, cookies, fetch }) => {
	try {
		const event: AlertDispatchEvent = await request.json();

		// The bot is a trusted service: build an explicit instance-key client,
		// forwarding the incoming host/proto so tenant resolution targets the
		// right tenant. (locals.apiClient carries only the end user's creds.)
		const extraHeaders: Record<string, string> = {
			"X-Forwarded-Proto": getOriginalProto(request),
		};
		const host = getEffectiveHost(request, cookies);
		if (host) extraHeaders["X-Forwarded-Host"] = host;

		const botApiClient = buildUnscopedBotApiClient(fetch, extraHeaders);
		await handleBotDispatch(event, botApiClient);
		return new Response(null, { status: 204 });
	} catch (err) {
		console.error("Bot dispatch failed:", err);
		return new Response(JSON.stringify({ error: "Dispatch failed" }), {
			status: 500,
			headers: { "Content-Type": "application/json" },
		});
	}
};
