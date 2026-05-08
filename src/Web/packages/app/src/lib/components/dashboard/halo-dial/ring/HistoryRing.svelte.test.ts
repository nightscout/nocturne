import { render } from "vitest-browser-svelte";
import { describe, it, expect } from "vitest";
import { HaloDialColorMode } from "$lib/api/generated/nocturne-api-client";
import HistoryRing from "./HistoryRing.svelte";
import { CENTER, RING_RADIUS } from "../geometry";

function distFromCenter(x: number, y: number): number {
	const dx = x - CENTER;
	const dy = y - CENTER;
	return Math.sqrt(dx * dx + dy * dy);
}

/**
 * Pull every absolute coordinate (M/L target points; the first two numbers
 * after each A command are the radii, so we skip those and read the final
 * pair). For our simple paths this is sufficient.
 */
function extractPoints(
	d: string,
): Array<{ x: number; y: number }> {
	const points: Array<{ x: number; y: number }> = [];
	const tokens = d.match(/[MLA]|-?\d+(?:\.\d+)?/g) ?? [];
	let i = 0;
	while (i < tokens.length) {
		const cmd = tokens[i];
		if (cmd === "M" || cmd === "L") {
			const x = parseFloat(tokens[i + 1]);
			const y = parseFloat(tokens[i + 2]);
			points.push({ x, y });
			i += 3;
		} else if (cmd === "A") {
			// rx ry x-axis-rot large-arc sweep x y
			const x = parseFloat(tokens[i + 6]);
			const y = parseFloat(tokens[i + 7]);
			points.push({ x, y });
			i += 8;
		} else {
			i += 1;
		}
	}
	return points;
}

describe("HistoryRing", () => {
	it("keeps every vertex on the ring when natural sweep fits the budget", () => {
		const { container } = render(HistoryRing, {
			historyValues: [100, 110, 120, 130],
			historyMinutes: 15,
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
		});

		const path = container.querySelector(
			"[data-testid='history-ring']",
		);
		expect(path).not.toBeNull();
		expect(path!.getAttribute("data-spiral-active")).toBe("false");

		const d = path!.getAttribute("d") ?? "";
		const points = extractPoints(d);
		expect(points.length).toBeGreaterThan(0);
		for (const p of points) {
			expect(distFromCenter(p.x, p.y)).toBeCloseTo(RING_RADIUS, 1);
		}
	});

	it("activates spiral fallback when historyMinutes exceeds the arc budget", () => {
		const values = Array.from({ length: 24 }, (_, i) => 100 + i);
		const { container } = render(HistoryRing, {
			historyValues: values,
			historyMinutes: 120,
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
		});

		const path = container.querySelector(
			"[data-testid='history-ring']",
		);
		expect(path).not.toBeNull();
		expect(path!.getAttribute("data-spiral-active")).toBe("true");

		const d = path!.getAttribute("d") ?? "";
		const points = extractPoints(d);
		const distances = points.map((p) =>
			distFromCenter(p.x, p.y),
		);
		expect(distances.some((dist) => dist > RING_RADIUS + 0.5)).toBe(
			true,
		);
		// Oldest vertex (rendered first) sits at RING_RADIUS + SPIRAL_MAX_OUTGROW_PX;
		// newest (rendered last) sits at RING_RADIUS — so radii decrease monotonically toward the newest.
		expect(distances[0]).toBeGreaterThan(
			distances[distances.length - 1],
		);
	});

	it("renders nothing when historyValues is empty", () => {
		const { container } = render(HistoryRing, {
			historyValues: [],
			historyMinutes: 15,
			predictionMinutes: 45,
			colorMode: HaloDialColorMode.Discrete,
		});

		const path = container.querySelector(
			"[data-testid='history-ring']",
		);
		expect(path).toBeNull();
	});
});
