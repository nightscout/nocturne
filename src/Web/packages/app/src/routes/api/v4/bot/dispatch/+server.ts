import type { RequestHandler } from "./$types";
import { handleBotDispatch } from "#lib/server/bot/index.js";
import { buildScopedBotApiClient } from "#lib/server/bot/api-client.js";
import { isTrustedInstanceRequest } from "#lib/server/instance-key.js";
import { parseAlertDispatchEvent } from "@nocturne/bot/parse-dispatch";

export const POST: RequestHandler = async ({ request, fetch }) => {
	// This is a public HTTP endpoint: the gateway routes /api/** here and
	// hooks.server.ts keeps /api/v4/bot out of the credential-scoping proxy and the
	// status probe (isWebAppApiPath). Dispatching posts an alert to a chat platform and
	// writes delivery state with instance-key (admin) privilege, so the caller
	// must present the instance key before anything else happens.
	if (!isTrustedInstanceRequest(request)) {
		return new Response(null, { status: 401 });
	}

	try {
		const event = parseAlertDispatchEvent(await request.json());
		if (!event) {
			return new Response(JSON.stringify({ error: "Invalid alert dispatch" }), {
				status: 400,
				headers: { "Content-Type": "application/json" },
			});
		}

		// The target tenant comes from the authenticated body, not from
		// X-Forwarded-Host: the edge gateway does not sanitize that header, so a
		// forwarded host lets the caller choose which tenant the admin-privileged
		// API calls land on. buildScopedBotApiClient derives the host from the
		// server's BASE_DOMAIN and this slug.
		// Shape-checked, not just present. The slug becomes a label in the Host header
		// buildScopedBotApiClient constructs, so a value containing a dot resolves as a
		// different host than intended — `<token>.share` would put the API into
		// share-resolution mode. Anything else non-conforming would reach undici and
		// throw, surfacing as a 500 rather than a bad request.
		if (!/^[a-z0-9][a-z0-9-]*$/.test(event.tenantSlug)) {
			return new Response(JSON.stringify({ error: "tenantSlug is required" }), {
				status: 400,
				headers: { "Content-Type": "application/json" },
			});
		}

		const botApiClient = buildScopedBotApiClient(fetch, event.tenantSlug);
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
