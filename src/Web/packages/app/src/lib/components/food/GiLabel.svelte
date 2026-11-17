<script lang="ts">
	import type { GiLevel } from './gi';

	interface Props {
		level: GiLevel;
	}

	let { level }: Props = $props();
</script>

{#if level === 'low'}Low{:else if level === 'medium'}Medium{:else}High{/if}
