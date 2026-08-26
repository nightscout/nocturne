import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { docsContentDir, manifestPath, repoRoot } from './paths.js';
import type { Manifest } from './types.js';

const OPENING_TAG = '<Screenshot';
const SELF_CLOSE = '/>';
const ID_ATTRIBUTE = /\bid\s*=\s*"([^"]*)"/;
const CALLOUTS_ATTRIBUTE = /\bcallouts\s*=\s*\{\s*\[([\s\S]*)\]\s*\}/;
const ANCHOR_ENTRY = /\banchor\s*:\s*"([^"]*)"/g;

async function* docPages(directory: string): AsyncGenerator<string> {
	for (const entry of await readdir(directory, { withFileTypes: true })) {
		const path = join(directory, entry.name);
		if (entry.isDirectory()) yield* docPages(path);
		else if (entry.name.endsWith('.svx')) yield path;
	}
}

/**
 * Reads the embeds out of a page's source without a Svelte parse, so a doc that points at an id or
 * an anchor the capture no longer produces fails review rather than the portal build. A usage this
 * cannot read apart — a computed id, an unterminated tag — is reported, not skipped: the check is
 * worth nothing if the shapes it misses are the ones that break.
 */
function findProblems(source: string, where: string, manifest: Manifest): string[] {
	const problems: string[] = [];

	for (let at = source.indexOf(OPENING_TAG); at !== -1; at = source.indexOf(OPENING_TAG, at + 1)) {
		const end = source.indexOf(SELF_CLOSE, at);
		const next = source.indexOf(OPENING_TAG, at + 1);
		if (end === -1 || (next !== -1 && end > next)) {
			problems.push(`${where}: <Screenshot> is not closed with "/>"`);
			continue;
		}

		const attributes = source.slice(at + OPENING_TAG.length, end);
		const id = ID_ATTRIBUTE.exec(attributes)?.[1];
		if (!id) {
			problems.push(`${where}: <Screenshot> has no literal id="..." attribute`);
			continue;
		}

		const entry = manifest[id];
		if (!entry) {
			problems.push(`${where}: <Screenshot id="${id}"> is not in the screenshots manifest`);
			continue;
		}

		if (!attributes.includes('callouts')) continue;
		const array = CALLOUTS_ATTRIBUTE.exec(attributes)?.[1];
		if (array === undefined) {
			problems.push(`${where}: <Screenshot id="${id}"> has a callouts attribute this check cannot read`);
			continue;
		}

		const anchors = [...array.matchAll(ANCHOR_ENTRY)].map(([, anchor]) => anchor);
		if (anchors.length === 0) {
			problems.push(`${where}: <Screenshot id="${id}"> declares callouts with no literal anchor`);
			continue;
		}
		for (const anchor of anchors) {
			if (!entry.anchors?.[anchor]) {
				const declared = Object.keys(entry.anchors ?? {}).join(', ') || 'none';
				problems.push(
					`${where}: <Screenshot id="${id}"> points at anchor "${anchor}"; declared anchors: ${declared}`,
				);
			}
		}
	}

	return problems;
}

/** Every docs embed whose id or anchor the manifest cannot satisfy. */
export async function findBrokenEmbeds(): Promise<string[]> {
	const manifest = JSON.parse(await readFile(manifestPath, 'utf8')) as Manifest;
	const problems: string[] = [];

	for await (const page of docPages(docsContentDir)) {
		const source = await readFile(page, 'utf8');
		problems.push(...findProblems(source, relative(repoRoot, page), manifest));
	}

	return problems;
}
