import { describe, it, expect } from "vitest";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * layerchart 2.x's <Text> renders its `value` prop and has no children snippet,
 * so `<Text ...>{expr}</Text>` silently paints nothing. Labels must pass `value`
 * or drop to a native <text> element.
 */

const roots = [
	fileURLToPath(new URL("../../../../src", import.meta.url)),
	fileURLToPath(new URL("../../../../../glucose-chart/src", import.meta.url)),
];

/**
 * Label sites still awaiting conversion in a separate in-flight change. Each is
 * asserted to still be an offender, so the entry fails once that change lands
 * rather than quietly outliving it.
 */
const pendingConversion = [
	"lib/components/dashboard/glucose-chart/markers/BolusMarker.svelte",
	"lib/components/dashboard/glucose-chart/markers/CarbMarker.svelte",
	"lib/components/dashboard/glucose-chart/markers/TrackerExpirationMarker.svelte",
	"lib/components/dashboard/glucose-chart/tracks/BasalTrack.svelte",
	"markers/BolusMarker.svelte",
	"markers/CarbMarker.svelte",
	"tracks/BasalTrack.svelte",
];

function svelteFiles(dir: string): string[] {
	const found: string[] = [];
	for (const entry of readdirSync(dir, { withFileTypes: true })) {
		const full = join(dir, entry.name);
		if (entry.isDirectory()) {
			if (entry.name === "node_modules" || entry.name === ".svelte-kit") continue;
			found.push(...svelteFiles(full));
		} else if (entry.name.endsWith(".svelte")) {
			found.push(full);
		}
	}
	return found;
}

/** A closing tag is the tell: only a children block needs one. */
function textUsages(source: string): { withChildren: number; total: number } {
	return {
		withChildren: source.match(/<\/Text>/g)?.length ?? 0,
		total: source.match(/<Text\b/g)?.length ?? 0,
	};
}

const scanned = roots
	.flatMap((root) =>
		svelteFiles(root).map((file) => ({
			id: relative(root, file).replaceAll("\\", "/"),
			...textUsages(readFileSync(file, "utf8")),
		})),
	)
	.filter((f) => f.total > 0);

describe("layerchart <Text> usage", () => {
	it("scans a non-empty set of <Text> call sites", () => {
		// A guard that discovers nothing passes vacuously.
		expect(scanned.length).toBeGreaterThan(0);
		expect(scanned.reduce((n, f) => n + f.total, 0)).toBeGreaterThan(0);
	});

	it("never gives <Text> snippet children", () => {
		const offenders = scanned
			.filter((f) => f.withChildren > 0)
			.map((f) => f.id)
			.filter((id) => !pendingConversion.includes(id));

		expect(offenders).toEqual([]);
	});

	it("keeps the pending-conversion list free of stale entries", () => {
		const stillOffending = scanned
			.filter((f) => f.withChildren > 0)
			.map((f) => f.id);

		expect(pendingConversion.filter((id) => !stillOffending.includes(id))).toEqual(
			[],
		);
	});
});
