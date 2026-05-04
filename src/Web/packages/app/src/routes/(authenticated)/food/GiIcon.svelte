<script lang="ts">
	import type { GiLevel } from './types';

	interface Props {
		level: GiLevel;
		size?: number;
	}

	let { level, size = 8 }: Props = $props();

	const clipId = `gi-clip-${crypto.randomUUID().slice(0, 8)}`;
</script>

<svg
	xmlns="http://www.w3.org/2000/svg"
	width={size}
	height={size}
	viewBox="0 0 16 16"
	fill="none"
	aria-hidden="true"
>
	{#if level === 'low'}
		<circle cx="8" cy="8" r="6.5" stroke="currentColor" stroke-width="1.5" />
	{:else if level === 'medium'}
		<defs>
			<clipPath id={clipId}>
				<rect x="0" y="8" width="16" height="8" />
			</clipPath>
		</defs>
		<circle cx="8" cy="8" r="6.5" stroke="currentColor" stroke-width="1.5" />
		<circle cx="8" cy="8" r="6.5" fill="currentColor" clip-path="url(#{clipId})" />
	{:else}
		<circle cx="8" cy="8" r="6.5" fill="currentColor" />
	{/if}
</svg>
