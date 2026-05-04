<script lang="ts">
	import type { GiLevel } from './types';
	import GiIcon from './GiIcon.svelte';

	interface Props {
		level: GiLevel;
	}

	let { level }: Props = $props();

	const labels: Record<GiLevel, string> = {
		low: 'Low GI',
		medium: 'Med GI',
		high: 'High GI'
	};
</script>

<span
	class="inline-flex items-center gap-1.5 rounded-full border border-border px-2 py-0.5 text-[11px] font-semibold tracking-wide text-muted-foreground"
>
	<GiIcon {level} />
	{labels[level]}
</span>
