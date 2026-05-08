<script lang="ts">
	import TrendChevron from "./TrendChevron.svelte";

	let {
		delta,
		color,
		stale,
	}: { delta: number; color: string; stale: boolean } = $props();
</script>

<svg viewBox="0 0 140 140" width="140" height="140">
	<TrendChevron {delta} {color} {stale} />
</svg>
