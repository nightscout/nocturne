import type { RequestHandler } from "./$types";
import { handleBotDispatch } from "$lib/server/bot";
import { buildScopedBotApiClient } from "$lib/server/bot/api-client";
import { isTrustedInstanceRequest } from "$lib/server/instance-key";
import type { AlertDispatchEvent } from "@nocturne/bot";

export const POST: RequestHandler = async ({ request, fetch }) => {
	// This is a public HTTP endpoint: the gateway routes /api/** here and
	// hooks.server.ts exempts /api/v4/bot from the site-security handle and the
	// credential-scoping proxy. Dispatching posts an alert to a chat platform and
	// writes delivery state with instance-key (admin) privilege, so the caller
	// must present the instance key before anything else happens.
	if (!isTrustedInstanceRequest(request)) {
		return new Response(null, { status: 401 });
	}

	try {
		const event: AlertDispatchEvent = await request.json();

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
		if (typeof event?.tenantSlug !== "string" || !/^[a-z0-9][a-z0-9-]*$/.test(event.tenantSlug)) {
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
