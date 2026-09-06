import { readdir, readFile } from 'node:fs/promises';
import { join, relative } from 'node:path';
import { docsContentDir, manifestPath, repoRoot } from './paths.js';
import type { Manifest } from './types.js';

const OPENING_TAG = '<Screenshot';
const TAG_NAME_CHARACTER = /[A-Za-z0-9_-]/;
const FENCED_CODE = /^[ \t]*(```|~~~)[\s\S]*?\1/gm;
const HTML_COMMENT = /<!--[\s\S]*?-->/g;
const INLINE_CODE = /`[^`\n]*`/g;
const ID_ATTRIBUTE = /(?<![\w-])id\s*=\s*(?:"([^"]*)"|'([^']*)')/;
const CALLOUTS_ATTRIBUTE = /(?<![\w-])callouts\s*=\s*\{\s*\[([\s\S]*)\]\s*\}/;
const HAS_CALLOUTS = /(?<![\w-])callouts\s*=/;
const ANCHOR_ENTRY = /\banchor\s*:\s*(?:"([^"]*)"|'([^']*)')/g;

async function* docPages(directory: string): AsyncGenerator<string> {
	for (const entry of await readdir(directory, { withFileTypes: true })) {
		const path = join(directory, entry.name);
		if (entry.isDirectory()) yield* docPages(path);
		else if (entry.name.endsWith('.svx')) yield path;
	}
}

/**
 * Offset of the ">" that closes this tag, skipping the ones inside a quoted or braced attribute
 * value. Scanning for the next "/>" instead would run past an unclosed tag into a later element's
 * attributes, and would stop early on an attribute value that contains "/>".
 */
function tagEnd(source: string, from: number): number {
	let quote: string | undefined;
	let depth = 0;

	for (let at = from; at < source.length; at++) {
		const character = source[at];
		if (quote) {
			if (character === quote) quote = undefined;
		} else if (character === '"' || character === "'") quote = character;
		else if (character === '{') depth++;
		else if (character === '}') depth--;
		else if (character === '>' && depth === 0) return at;
	}

	return -1;
}

/**
 * Reads the embeds out of a page's source without a Svelte parse, so a doc that points at an id or
 * an anchor the capture no longer produces fails review rather than the portal build. A usage this
 * cannot read apart — a computed id, an unterminated tag — is reported, not skipped: the check is
 * worth nothing if the shapes it misses are the ones that break. Code fences and comments are cut
 * first, so a page that documents the component is not held to the manifest.
 */
function findProblems(page: string, where: string, manifest: Manifest): string[] {
	const problems: string[] = [];
	let source = page;
	let previous: string;
	do {
		previous = source;
		source = source.replace(FENCED_CODE, '').replace(HTML_COMMENT, '').replace(INLINE_CODE, '');
	} while (source !== previous);

	for (let at = source.indexOf(OPENING_TAG); at !== -1; at = source.indexOf(OPENING_TAG, at + 1)) {
		const after = source[at + OPENING_TAG.length];
		if (after !== undefined && TAG_NAME_CHARACTER.test(after)) continue;

		const end = tagEnd(source, at + OPENING_TAG.length);
		if (end === -1) {
			problems.push(`${where}: <Screenshot> has no closing ">"`);
			continue;
		}

		const tag = source.slice(at + OPENING_TAG.length, end).trimEnd();
		if (!tag.endsWith('/')) {
			problems.push(`${where}: <Screenshot> is not closed with "/>"`);
			continue;
		}

		const attributes = tag.slice(0, -1);
		const idMatch = ID_ATTRIBUTE.exec(attributes);
		const id = idMatch?.[1] ?? idMatch?.[2];
		if (!id) {
			problems.push(`${where}: <Screenshot> has no literal id="..." attribute`);
			continue;
		}

		const entry = manifest[id];
		if (!entry) {
			problems.push(`${where}: <Screenshot id="${id}"> is not in the screenshots manifest`);
			continue;
		}

		if (!HAS_CALLOUTS.test(attributes)) continue;
		const array = CALLOUTS_ATTRIBUTE.exec(attributes)?.[1];
		if (array === undefined) {
			problems.push(`${where}: <Screenshot id="${id}"> has a callouts attribute this check cannot read`);
			continue;
		}

		const anchors = [...array.matchAll(ANCHOR_ENTRY)].map(
			([, doubleQuoted, singleQuoted]) => doubleQuoted ?? singleQuoted,
		);
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
	let scanned = 0;

	for await (const page of docPages(docsContentDir)) {
		scanned++;
		const source = await readFile(page, 'utf8');
		problems.push(...findProblems(source, relative(repoRoot, page), manifest));
	}

	// A check that reaches no pages passes for the wrong reason, and would keep passing if the docs
	// moved out from under it.
	if (scanned === 0) problems.push(`${relative(repoRoot, docsContentDir)} holds no .svx pages to check`);

	return problems;
}
