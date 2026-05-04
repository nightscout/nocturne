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

<div class="header-row">
	<span></span>

	<Button variant="ghost" size="sm" class="h-auto p-0 text-[11px] font-semibold uppercase tracking-wider hover:bg-transparent {sort === 'name' ? 'text-foreground' : ''}" onclick={() => onsort('name')}>
		Name
		{#if sort === 'name'}
			<ChevronDown class="h-2.5 w-2.5" />
		{/if}
	</Button>

	<Button
		variant="ghost"
		size="sm"
		class="h-auto p-0 text-[11px] font-semibold uppercase tracking-wider hover:bg-transparent"
		style={sort === 'carbs' ? 'color: var(--carbs-strong)' : undefined}
		onclick={() => onsort('carbs')}
	>
		Carbs
		{#if sort === 'carbs'}
			<ChevronDown class="h-2.5 w-2.5" />
		{/if}
	</Button>

	<span class="col-label">Portion</span>

	<span class="col-label">GI</span>

	<span class="col-label" style="text-align: right;">Energy</span>

	<span></span>
</div>

<style>
	.header-row {
		display: grid;
		grid-template-columns: 24px 1fr 110px 130px 90px 70px 24px;
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
		font-size: 11px;
		font-weight: 600;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		color: oklch(from var(--muted-foreground) l c h / 0.7);
	}
</style>
