import { execFile } from 'node:child_process';
import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { imagesDir, repoRoot } from './paths.js';

const REFERENCE_PATTERN = /packages\/screenshots\/images\/([A-Za-z0-9._-]+)/g;
const LIST_BUFFER_BYTES = 16 * 1024 * 1024;

const run = promisify(execFile);

/**
 * Tracked files only. A checkout can hold sibling git worktrees and untracked scratch under it,
 * and neither is documentation this run is allowed to fail on.
 */
async function markdownFiles(): Promise<string[]> {
	const { stdout } = await run('git', ['ls-files', '-z', '--', '*.md'], {
		cwd: repoRoot,
		maxBuffer: LIST_BUFFER_BYTES,
	});
	return stdout.split('\0').filter(Boolean);
}

/**
 * Every image the repo's markdown points at, and whether it is on disk. A miss means the docs
 * outran the capture (or an id was renamed) — the caller is expected to fail rather than warn.
 */
export async function findBrokenReferences(): Promise<string[]> {
	const captured = new Set(await readdir(imagesDir));
	const files = await markdownFiles();
	const broken: string[] = [];

	// See findBrokenEmbeds on why an empty scan is a failure rather than a pass.
	if (files.length === 0) broken.push('git ls-files matched no markdown to check');

	for (const file of files) {
		let markdown: string;
		try {
			markdown = await readFile(join(repoRoot, file), 'utf8');
		} catch {
			broken.push(`${file} is tracked but not on disk, so its references cannot be read`);
			continue;
		}
		for (const [, image] of markdown.matchAll(REFERENCE_PATTERN)) {
			if (!captured.has(image)) {
				broken.push(`${file} references images/${image}, which was not captured`);
			}
		}
	}

	return broken;
}
