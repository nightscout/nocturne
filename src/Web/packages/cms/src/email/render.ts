import { createHash, timingSafeEqual } from 'node:crypto';
import type { RequestHandler } from '@sveltejs/kit';
import { z } from 'zod';
import type { EmailComponentMap } from './component-map.ts';

export interface EmailRenderOptions {
	/** Email component substitution map (strict allowlist) */
	componentMap: EmailComponentMap;
	/** Shared secret for authenticating internal requests */
	secret: string;
	/** Render function: takes template name + locale + data, returns HTML */
	renderTemplate: (
		template: string,
		locale: string,
		data: Record<string, string>,
	) => Promise<string>;
}

const renderRequestSchema = z.object({
	template: z.string().min(1),
	locale: z.string().min(1),
	data: z.record(z.string(), z.string()).nullish(),
});

/**
 * Compares digests rather than the raw strings so the comparison takes the same time whatever
 * the header's length or content.
 */
export function isAuthorised(presented: string | null, secret: string): boolean {
	if (!secret || !presented) return false;
	const expected = createHash('sha256').update(secret).digest();
	const actual = createHash('sha256').update(presented).digest();
	return timingSafeEqual(expected, actual);
}

/**
 * Creates a SvelteKit request handler for dynamic email rendering.
 * The .NET backend POSTs to this endpoint with:
 *   { template: "weekly-summary", locale: "en", data: { userName: "Rhys", ... } }
 * and receives rendered HTML.
 */
export function createEmailRenderHandler(options: EmailRenderOptions): RequestHandler {
	return ({ request }) => handleEmailRenderRequest(request, options);
}

export async function handleEmailRenderRequest(
	request: Request,
	{ secret, renderTemplate }: EmailRenderOptions,
): Promise<Response> {
	if (!isAuthorised(request.headers.get('x-email-render-secret'), secret)) {
		return new Response('Unauthorized', { status: 401 });
	}

	const parsed = renderRequestSchema.safeParse(await request.json().catch(() => null));
	if (!parsed.success) {
		return new Response(
			JSON.stringify({ error: 'Missing required fields: template, locale' }),
			{ status: 400, headers: { 'Content-Type': 'application/json' } },
		);
	}

	const { template, locale, data } = parsed.data;

	try {
		const html = await renderTemplate(template, locale, data ?? {});

		return new Response(html, {
			headers: { 'Content-Type': 'text/html; charset=utf-8' },
		});
	} catch (error) {
		const message = error instanceof Error ? error.message : 'Unknown error';
		return new Response(JSON.stringify({ error: message }), {
			status: 500,
			headers: { 'Content-Type': 'application/json' },
		});
	}
}
