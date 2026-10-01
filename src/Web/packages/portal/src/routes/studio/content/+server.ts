import { dev } from '$app/environment';
import { json, error } from '@sveltejs/kit';
import { readdir, readFile } from 'node:fs/promises';
import { resolve, basename } from 'node:path';
import { parseFrontmatter } from '@nocturne/cms/blog/manifest';
import type { RequestHandler } from './$types';

export const prerender = false;

const CONTENT_DIR = resolve('src/content/blog');

export const GET: RequestHandler = async () => {
	if (!dev) {
		throw error(403, 'Studio content API is only available in development mode');
	}

	const files = await readdir(CONTENT_DIR).catch(() => []);
	const svxFiles = files.filter((f) => f.endsWith('.svx'));

	const posts = [];
	for (const file of svxFiles) {
		// eslint-disable-next-line security/detect-non-literal-fs-filename -- file is an entry readdir listed in CONTENT_DIR, not request input
		const content = await readFile(resolve(CONTENT_DIR, file), 'utf-8');
		const meta = parseFrontmatter(content, file);
		const slug = basename(file, '.svx');

		// Split frontmatter from body
		const bodyMatch = content.match(/^---\n[\s\S]*?\n---\n?([\s\S]*)$/);
		const body = bodyMatch?.[1]?.trim() ?? '';

		posts.push({
			slug,
			content: body,
			metadata: meta ?? { title: slug, slug },
		});
	}

	return json(posts);
};
