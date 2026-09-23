import { dev } from '$app/environment';
import { env } from '$env/dynamic/private';
import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';

export const prerender = false;

/**
 * Dev-only bridge from the Studio to the content-contribution relay: the
 * portal is a static site with no production server, so proposing a PR from
 * the Studio goes through this dev-server endpoint, which forwards to
 * nocturne.run's anonymous content relay (or a local API via
 * CONTENT_CONTRIBUTION_URL when developing the flow end to end).
 *
 * The Studio is a blog editor (blog metadata fields, blog collection), so the
 * bridge only ever builds a blog path. The API's allowlist also admits
 * `content/docs/**` because the relay serves any contribution tool, not just
 * this one; nothing here is missing.
 */
const CONTENT_DIR_PREFIX = 'src/Web/packages/portal/src/content/blog';

export const POST: RequestHandler = async ({ request, fetch }) => {
	if (!dev) {
		throw error(403, 'Studio propose API is only available in development mode');
	}

	const body = await request.json();
	const slug = String(body.slug ?? '');

	const target = env.CONTENT_CONTRIBUTION_URL || 'https://nocturne.run/api/v4/content/relay';
	const response = await fetch(target, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json' },
		body: JSON.stringify({
			path: `${CONTENT_DIR_PREFIX}/${slug}.svx`,
			content: String(body.content ?? ''),
			title: String(body.title ?? slug),
			contributor: body.contributor,
			note: body.note ?? null,
		}),
	});

	if (!response.ok) {
		// The API owns the path and slug rules, so its rejection reason is the
		// only useful message here. Unwrap the ProblemDetails so a dev sees the
		// sentence rather than the envelope.
		const raw = await response.text().catch(() => '');
		let detail = raw;
		try {
			detail = JSON.parse(raw).detail || raw;
		} catch {
			// Not ProblemDetails; the raw body is the best available message.
		}
		const status = response.status === 422 || response.status === 400 ? response.status : 502;
		throw error(status, detail || 'The contribution was rejected');
	}

	return json(await response.json());
};
