import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { imagesDir, repoRoot } from './paths.js';

const REFERENCE_PATTERN = /packages\/screenshots\/images\/([A-Za-z0-9._-]+)/g;
const SKIPPED_DIRECTORIES = new Set([
	'.git',
	'.svelte-kit',
	'bin',
	'build',
	'dist',
	'node_modules',
	'obj',
]);

async function* markdownFiles(directory: string): AsyncGenerator<string> {
	for (const entry of await readdir(directory, { withFileTypes: true })) {
		const path = join(directory, entry.name);
		if (entry.isDirectory()) {
			if (!SKIPPED_DIRECTORIES.has(entry.name)) yield* markdownFiles(path);
		} else if (entry.name.toLowerCase().endsWith('.md')) {
			yield path;
		}
	}
}

/**
 * Every image the repo's markdown points at, and whether it is on disk. A miss means the docs
 * outran the capture (or an id was renamed) — the caller is expected to fail rather than warn.
 */
export async function findBrokenReferences(): Promise<string[]> {
	const captured = new Set(await readdir(imagesDir));
	const broken: string[] = [];

	for await (const file of markdownFiles(repoRoot)) {
		const markdown = await readFile(file, 'utf8');
		for (const [, image] of markdown.matchAll(REFERENCE_PATTERN)) {
			if (!captured.has(image)) {
				broken.push(`${relative(repoRoot, file)} references images/${image}, which was not captured`);
			}
		}
	}

	return broken;
}
