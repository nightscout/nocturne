<script lang="ts">
	import { ChevronDown } from 'lucide-svelte';
	import type { SortMode } from './types';
	import { Button } from '$lib/components/ui/button';

	interface Props {
		sort: SortMode;
		onsort: (sort: SortMode) => void;
	}

	const { sort, onsort }: Props = $props();
</script>

<div class="header-row grid grid-cols-[24px_1fr_72px_24px] sm:grid-cols-[24px_1fr_110px_130px_90px_70px_24px]">
	<span></span>

	<Button variant="subtle" size="inline-xs" onclick={() => onsort('name')}>
		<span class="contents {sort === 'name' ? 'text-foreground' : ''}">
			Name
			{#if sort === 'name'}
				<ChevronDown class="h-2.5 w-2.5" />
			{/if}
		</span>
	</Button>

	<Button variant="subtle" size="inline-xs" onclick={() => onsort('carbs')}>
		<span class="contents {sort === 'carbs' ? 'text-entry-carbs' : ''}">
			Carbs
			{#if sort === 'carbs'}
				<ChevronDown class="h-2.5 w-2.5" />
			{/if}
		</span>
	</Button>

	<span class="col-label text-xs max-sm:hidden">Portion</span>

	<span class="col-label text-xs max-sm:hidden">GI</span>

	<span class="col-label text-right text-xs max-sm:hidden">Energy</span>

	<span></span>
</div>

<style>
	.header-row {
		text-transform: uppercase;
		letter-spacing: 0.05em;
		display: grid;
		gap: 12px;
		padding: 10px 16px;
		align-items: center;
		position: sticky;
		top: 0;
		z-index: 5;
		background: var(--card);
		border-bottom: 1px solid var(--border);
	}

	.col-label {
		font-weight: 600;
		color: oklch(from var(--muted-foreground) l c h / 0.7);
	}
</style>
