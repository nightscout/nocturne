<script lang="ts">
	// Fallback to <path> due to no polar Chart context yet — revisit when HaloDial is wired in Task 4.8.
	import { Tween } from "svelte/motion";
	import { cubicOut } from "svelte/easing";
	import { browser } from "$app/environment";
	import { polar } from "../geometry";
	import { HaloDialArcElement } from "../config";

	interface Props {
		element: HaloDialArcElement;
		value: number | null;
		max: number;
		side: "left" | "right";
	}

	const { element, value, max, side }: Props = $props();

	/** Component-specific inner-arc radius (per design: r = 42). */
	const RADIUS = 42;
	const MAX_SWEEP_DEG = 130;
	const STROKE_WIDTH = 4;
	const TRACK_OPACITY = 0.15;
	const START_ANGLE = 180; // 6 o'clock

	// Captured at mount; preference changes mid-session won't update the tween duration.
	const reducedMotion =
		browser &&
		window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

	function colorFor(el: HaloDialArcElement): string {
		switch (el) {
			case HaloDialArcElement.Iob:
				return "var(--insulin)";
			case HaloDialArcElement.Cob:
				return "var(--carbs)";
			case HaloDialArcElement.BasalPercent:
				return "var(--insulin-basal)";
			case HaloDialArcElement.Sensitivity:
				// TODO: --sensitivity CSS var; using --accent until token added
				return "var(--accent)";
			default:
				return "currentColor";
		}
	}

	const stroke = $derived(colorFor(element));

	// left-side arc grows visually toward 9 o'clock (CCW); right-side grows toward 3 o'clock (CW).
	// `direction` / sweepFlag values are SVG-coord-space.
	const direction = $derived(side === "left" ? 1 : -1);
	/** SVG arc sweep-flag: 1 for CW, 0 for CCW. */
	const sweepFlag = $derived(side === "left" ? 1 : 0);

	const targetSweep = $derived.by(() => {
		if (value === null || !Number.isFinite(value) || value <= 0 || max <= 0) {
			return 0;
		}
		const ratio = Math.min(1, value / max);
		return ratio * MAX_SWEEP_DEG;
	});

	const tweenedSweep = Tween.of(() => targetSweep, {
		duration: reducedMotion ? 0 : 600,
		easing: cubicOut,
	});

	function buildArc(sweep: number): string {
		const start = polar(START_ANGLE, RADIUS);
		const end = polar(START_ANGLE + direction * sweep, RADIUS);
		const largeArc = sweep > 180 ? 1 : 0;
		return `M ${start.x.toFixed(3)} ${start.y.toFixed(3)} A ${RADIUS} ${RADIUS} 0 ${largeArc} ${sweepFlag} ${end.x.toFixed(3)} ${end.y.toFixed(3)}`;
	}

	const trackD = $derived(buildArc(MAX_SWEEP_DEG));
	const valueD = $derived(buildArc(tweenedSweep.current));
</script>

<g data-testid="inner-arc-slot" data-element={element} data-side={side}>
	<path
		d={trackD}
		fill="none"
		stroke={stroke}
		stroke-opacity={TRACK_OPACITY}
		stroke-width={STROKE_WIDTH}
		stroke-linecap="round"
		data-testid="inner-arc-track"
	/>
	<path
		d={valueD}
		fill="none"
		stroke={stroke}
		stroke-width={STROKE_WIDTH}
		stroke-linecap="round"
		data-testid="inner-arc-value"
		data-target-sweep-deg={targetSweep.toFixed(3)}
		data-sweep-deg={tweenedSweep.current.toFixed(3)}
	/>
</g>
