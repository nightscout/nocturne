<script lang="ts">
	import { Tween } from "svelte/motion";
	import { cubicOut } from "svelte/easing";
	import { browser } from "$app/environment";
	import { trendAngle, CENTER, RING_RADIUS } from "../geometry";

	let {
		delta,
		color,
		stale,
	}: { delta: number; color: string; stale: boolean } = $props();

	// Captured at mount; preference changes mid-session won't update the tween duration.
	const reducedMotion =
		browser &&
		window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;

	const angle = Tween.of(() => trendAngle(delta), {
		duration: reducedMotion ? 0 : 600,
		easing: cubicOut,
	});
</script>

{#if !stale}
	<g
		transform="rotate({angle.current} {CENTER} {CENTER}) translate({CENTER + RING_RADIUS + 5} {CENTER})"
	>
		<path d="M 0 -6.5 Q 5 -3.2 10 0 Q 5 3.2 0 6.5 Z" fill={color} />
	</g>
{/if}
