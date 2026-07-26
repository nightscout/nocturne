<script lang="ts">
	import { Tween, prefersReducedMotion } from "svelte/motion";
	import { cubicOut } from "svelte/easing";
	import { trendAngle, CENTER, RING_RADIUS } from "../geometry";

	let {
		delta,
		color,
		stale,
	}: { delta: number; color: string; stale: boolean } = $props();


	const angle = Tween.of(() => trendAngle(delta), {
		// Reactive, so toggling the OS setting takes effect without a reload.
		duration: prefersReducedMotion.current ? 0 : 600,
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
