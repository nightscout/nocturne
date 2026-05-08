<script lang="ts">
	// Fallback to <path> due to no polar Chart context yet — revisit when HaloDial is wired in Task 4.8.
	import {
		RING_RADIUS,
		polar,
		predictionSweepDeg,
	} from "../geometry";
	import { bgColor } from "../colors";
	import {
		predictionDashArray,
		predictionLineCap,
	} from "../dash-mapping";
	import type { HaloDialColorMode } from "../config";
	import type { PumpModeState } from "$lib/api";

	interface Props {
		currentBg: number;
		predictionValues: number[] | null | undefined;
		predictionMinutes: number;
		colorMode: HaloDialColorMode;
		pumpMode: PumpModeState | null;
		gradientId?: string;
	}

	const {
		currentBg,
		predictionValues,
		predictionMinutes,
		colorMode,
		pumpMode,
		gradientId = "prediction-gradient",
	}: Props = $props();

	const STROKE_WIDTH = 5;

	const sweep = $derived(predictionSweepDeg(predictionMinutes));

	const hasValues = $derived(
		!!predictionValues && predictionValues.length > 0,
	);

	// vertices = [currentBg, ...predictionValues] when non-empty; distributed evenly across [0, sweep].
	const vertices = $derived.by(() => {
		if (!predictionValues || predictionValues.length === 0) return [];
		const values = [currentBg, ...predictionValues];
		const lastIdx = values.length - 1;
		return values.map((value, i) => {
			const angle = lastIdx === 0 ? 0 : (i / lastIdx) * sweep;
			const { x, y } = polar(angle, RING_RADIUS);
			return { angle, x, y, value };
		});
	});

	const pathD = $derived.by(() => {
		if (vertices.length < 2) return "";
		const head = vertices[0];
		let d = `M ${head.x.toFixed(3)} ${head.y.toFixed(3)}`;
		for (let i = 1; i < vertices.length; i++) {
			const v = vertices[i];
			d += ` A ${RING_RADIUS} ${RING_RADIUS} 0 0 1 ${v.x.toFixed(3)} ${v.y.toFixed(3)}`;
		}
		return d;
	});

	const gradientStops = $derived.by(() =>
		vertices.map((v, i) => ({
			offset: vertices.length <= 1 ? 0 : i / (vertices.length - 1),
			color: bgColor(v.value, colorMode),
		})),
	);

	const dashArray = $derived(predictionDashArray(pumpMode));
	const lineCap = $derived(predictionLineCap(pumpMode));

	// Linear gradient direction: roughly along the path's bounding box. Since
	// the prediction sweeps clockwise from 12 o'clock, x grows then shrinks;
	// a horizontal gradient is the closest pragmatic approximation. When the
	// polar <Chart> wraps this in Task 4.8, swap to per-vertex Spline coloring.
	const backdropD = $derived.by(() => {
		if (hasValues) return "";
		// Half-circle ghosted backdrop (full sweep up to MIN_GAP cap).
		const end = polar(sweep, RING_RADIUS);
		const head = polar(0, RING_RADIUS);
		const largeArc = sweep > 180 ? 1 : 0;
		return `M ${head.x.toFixed(3)} ${head.y.toFixed(3)} A ${RING_RADIUS} ${RING_RADIUS} 0 ${largeArc} 1 ${end.x.toFixed(3)} ${end.y.toFixed(3)}`;
	});
</script>

{#if hasValues && vertices.length >= 2}
	<defs>
		<linearGradient
			id={gradientId}
			gradientUnits="userSpaceOnUse"
			x1={vertices[0]?.x ?? 0}
			y1={vertices[0]?.y ?? 0}
			x2={vertices[vertices.length - 1]?.x ?? 0}
			y2={vertices[vertices.length - 1]?.y ?? 0}
		>
			{#each gradientStops as stop, i (i)}
				<stop offset={stop.offset} stop-color={stop.color} />
			{/each}
		</linearGradient>
	</defs>
	<path
		d={pathD}
		fill="none"
		stroke={`url(#${gradientId})`}
		stroke-width={STROKE_WIDTH}
		stroke-linecap={lineCap}
		stroke-dasharray={dashArray}
		data-testid="prediction-ring"
	/>
{:else}
	<path
		d={backdropD}
		fill="none"
		stroke="var(--foreground)"
		stroke-opacity="0.08"
		stroke-width={STROKE_WIDTH}
		stroke-linecap="butt"
		data-testid="prediction-ring-backdrop"
	/>
{/if}
