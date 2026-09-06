import { execFileSync } from 'node:child_process';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import { join, relative, sep } from 'node:path';
import sharp from 'sharp';
import { imagesDir, repoRoot } from './paths.js';

/**
 * Lossy WebP re-quantises a whole compression block around one changed glyph, so an exact
 * comparison reads a clock ticking over as a rewritten screenshot. Calibrated against a real
 * recapture: the counts collapse between 0 and 8 and then flatten, and 8 separates a shot whose
 * data moved from one whose timestamp did by two orders of magnitude. Higher starts eating the
 * anti-aliased edges of text that genuinely changed.
 */
const CHANNEL_TOLERANCE = 8;

const BASE = 'HEAD';
/** The images directory also carries a .gitkeep, which is not a capture. */
const EXTENSION = '.webp';

type Status = 'changed' | 'resized' | 'added' | 'deleted' | 'unreadable';

interface Row {
	file: string;
	status: Status;
	detail: string;
	/** Structural findings have no percentage to sort on and belong above the ones that do. */
	rank: number;
}

interface Totals {
	changedPixels: number;
	comparedPixels: number;
	identical: number;
}

function git(args: string[]): Buffer {
	return execFileSync('git', args, { cwd: repoRoot, maxBuffer: 256 * 1024 * 1024 });
}

async function decode(input: Buffer): Promise<{ data: Buffer; width: number; height: number }> {
	const { data, info } = await sharp(input)
		.ensureAlpha()
		.raw()
		.toBuffer({ resolveWithObject: true });
	return { data, width: info.width, height: info.height };
}

function countChangedPixels(before: Buffer, after: Buffer): number {
	let changed = 0;
	for (let i = 0; i < before.length; i += 4) {
		if (
			Math.abs(before[i] - after[i]) > CHANNEL_TOLERANCE ||
			Math.abs(before[i + 1] - after[i + 1]) > CHANNEL_TOLERANCE ||
			Math.abs(before[i + 2] - after[i + 2]) > CHANNEL_TOLERANCE ||
			Math.abs(before[i + 3] - after[i + 3]) > CHANNEL_TOLERANCE
		) {
			changed++;
		}
	}
	return changed;
}

function percentage(changed: number, total: number): string {
	if (total === 0) return '0%';
	const value = (changed / total) * 100;
	if (changed > 0 && value < 0.01) return '<0.01%';
	return `${value.toFixed(2)}%`;
}

async function compare(
	path: string,
	file: string,
	totals: Totals,
	restoreIdentical: boolean,
): Promise<Row | null> {
	const committed = git(['show', `${BASE}:${path}`]);
	const current = await readFile(join(imagesDir, file));
	if (committed.equals(current)) {
		const { width = 0, height = 0 } = await sharp(current).metadata();
		totals.comparedPixels += width * height;
		totals.identical++;
		return null;
	}

	let before: Awaited<ReturnType<typeof decode>>;
	let after: Awaited<ReturnType<typeof decode>>;
	try {
		[before, after] = await Promise.all([decode(committed), decode(current)]);
	} catch (error) {
		return {
			file,
			status: 'unreadable',
			detail: error instanceof Error ? error.message : String(error),
			rank: Number.POSITIVE_INFINITY,
		};
	}

	if (before.width !== after.width || before.height !== after.height) {
		return {
			file,
			status: 'resized',
			detail: `${before.width}x${before.height} -> ${after.width}x${after.height}`,
			rank: Number.POSITIVE_INFINITY,
		};
	}

	const pixels = before.width * before.height;
	const changed = countChangedPixels(before.data, after.data);
	totals.changedPixels += changed;
	totals.comparedPixels += pixels;
	if (changed === 0) {
		// Same pixels, different bytes — an encoder version re-quantised the file. Restoring the
		// committed bytes keeps a pixel-noop out of git history and out of the drift gate.
		if (restoreIdentical) await writeFile(join(imagesDir, file), committed);
		totals.identical++;
		return null;
	}
	return {
		file,
		status: 'changed',
		detail: percentage(changed, pixels),
		rank: changed / pixels,
	};
}

async function collect(restoreIdentical: boolean): Promise<{ rows: Row[]; totals: Totals }> {
	const prefix = relative(repoRoot, imagesDir).split(sep).join('/');
	const committed = git(['ls-tree', '-r', '--name-only', BASE, '--', prefix])
		.toString('utf8')
		.split('\n')
		.filter((path) => path.endsWith(EXTENSION));
	const present = new Set(
		(await readdir(imagesDir, { recursive: true }))
			.map((file) => file.split(sep).join('/'))
			.filter((file) => file.endsWith(EXTENSION)),
	);

	const totals: Totals = { changedPixels: 0, comparedPixels: 0, identical: 0 };
	const rows: Row[] = [];

	for (const path of committed) {
		const file = path.slice(prefix.length + 1);
		if (!present.delete(file)) {
			rows.push({
				file,
				status: 'deleted',
				detail: '-',
				rank: Number.POSITIVE_INFINITY,
			});
			continue;
		}
		const row = await compare(path, file, totals, restoreIdentical);
		if (row) rows.push(row);
	}

	for (const file of present) {
		let detail: string;
		let status: Status = 'added';
		try {
			const { width, height } = await decode(await readFile(join(imagesDir, file)));
			detail = `${width}x${height}`;
		} catch (error) {
			status = 'unreadable';
			detail = error instanceof Error ? error.message : String(error);
		}
		rows.push({
			file,
			status,
			detail,
			rank: Number.POSITIVE_INFINITY,
		});
	}

	rows.sort((left, right) => right.rank - left.rank || left.file.localeCompare(right.file));
	return { rows, totals };
}

function render(rows: Row[], totals: Totals): string {
	const compared = rows.length + totals.identical;
	if (rows.length === 0) {
		return `No screenshot drift: all ${compared} images match ${BASE}.`;
	}

	const counts = new Map<Status, number>();
	for (const row of rows) counts.set(row.status, (counts.get(row.status) ?? 0) + 1);
	const summary = [...counts]
		.map(([status, count]) => `${count} ${status}`)
		.join(', ');

	return [
		'| Image | Status | Changed |',
		'| --- | --- | --- |',
		...rows.map((row) => `| \`${row.file}\` | ${row.status} | ${row.detail} |`),
		'',
		`Against ${BASE}: ${summary}, ${totals.identical} unchanged. `
			+ `${percentage(totals.changedPixels, totals.comparedPixels)} of all compared pixels differ.`,
	].join('\n');
}

async function main(): Promise<void> {
	const flag = process.argv.indexOf('--output');
	const output = flag === -1 ? null : process.argv[flag + 1];
	if (flag !== -1 && !output) {
		console.error('--output needs a file path.');
		process.exit(1);
	}
	const restoreIdentical = process.argv.includes('--restore-identical');

	const { rows, totals } = await collect(restoreIdentical);
	const markdown = render(rows, totals);
	console.log(markdown);
	if (output) await writeFile(output, `${markdown}\n`);
}

await main();
