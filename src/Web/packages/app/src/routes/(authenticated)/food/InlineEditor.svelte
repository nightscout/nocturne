<script lang="ts">
	import type { Food } from '$api';
	import { Trash2, Check } from 'lucide-svelte';
	import GiIcon from './GiIcon.svelte';
	import { getFoodState } from './food-context.js';
	import { giFromInt, giToInt } from './types.js';
	import type { GiLevel } from './types.js';
	import { FOOD_UNITS } from '$lib/components/food';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import * as ToggleGroup from '$lib/components/ui/toggle-group';
	import { Separator } from '$lib/components/ui/separator';

	interface Props {
		food: Food;
		onsave: (draft: Food) => void;
		oncancel: () => void;
	}

	const { food, onsave, oncancel }: Props = $props();
	const foodState = getFoodState();

	// Intentionally capture initial food value - draft is the local editing copy
	let draft = $state<Food>({ ...food });
	let confirming = $state(false);
	let attributionCount = $state(0);

	const giLevels: GiLevel[] = ['low', 'medium', 'high'];

	const subcategories = $derived.by(() => {
		if (!draft.category) return [];
		const subs = new Set<string>();
		for (const f of foodState.foods) {
			if (f.category === draft.category && f.subcategory) {
				subs.add(f.subcategory);
			}
		}
		return [...subs].sort();
	});

	async function handleDeleteClick() {
		if (!food._id) return;
		confirming = true;
		attributionCount = await foodState.getAttributionCount(food._id);
	}

	async function confirmDelete() {
		if (!food._id) return;
		await foodState.deleteFood(food._id, 'clear');
	}
</script>

<div class="border-y border-border px-4 py-4" style="background: oklch(0.17 0.03 263)">
	<!-- Delete confirmation bar -->
	{#if confirming}
		<div class="mb-4 flex items-center gap-3 rounded-lg px-4 py-3" style="background: oklch(0.25 0.06 25 / 0.5); border: 1px solid oklch(0.6 0.2 25 / 0.3)">
			<Trash2 size={16} class="shrink-0 text-red-400" />
			<span class="text-sm">
				Delete <strong>{food.name}</strong>?
				{#if attributionCount > 0}
					<span class="ml-1 text-muted-foreground">Used in {attributionCount} {'treatment' + (attributionCount === 1 ? '' : 's')}.</span>
				{/if}
			</span>
			<div class="ml-auto flex items-center gap-2">
				<Button variant="ghost" size="sm" onclick={() => { confirming = false; }}>Cancel</Button>
				<Button variant="destructive" size="sm" onclick={confirmDelete}><Trash2 class="h-3 w-3" /> Delete</Button>
			</div>
		</div>
	{/if}

	<!-- Section 1: Name, Carbs, Portion, Unit -->
	<div class="grid gap-4" style="grid-template-columns: 1.6fr 1fr 1fr 1fr">
		<!-- Name -->
		<div class="flex flex-col gap-1.5">
			<span class="text-muted-foreground font-semibold" style="font-size: 11px">Name *</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="text"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.name}
					required
				/>
			</div>
		</div>

		<!-- Carbs -->
		<div class="flex flex-col gap-1.5">
			<span class="font-semibold" style="font-size: 11px; color: var(--carbs)">Carbs *</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid var(--carbs-border-strong); background: var(--carbs-bg)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.carbs}
					min="0"
					step="0.1"
				/>
				<span class="ml-2 shrink-0 text-xs" style="color: var(--carbs)">g</span>
			</div>
			<span class="text-muted-foreground" style="font-size: 10px">per {draft.portion ?? 100} {draft.unit ?? 'g'}</span>
		</div>

		<!-- Portion -->
		<div class="flex flex-col gap-1.5">
			<span class="text-muted-foreground font-semibold" style="font-size: 11px">Portion *</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.portion}
					min="0"
					step="1"
				/>
				<span class="ml-2 shrink-0 text-xs text-muted-foreground">{draft.unit ?? 'g'}</span>
			</div>
		</div>

		<!-- Unit -->
		<div class="flex flex-col gap-1.5">
			<span class="text-muted-foreground font-semibold" style="font-size: 11px">Unit</span>
			<ToggleGroup.Root type="single" value={draft.unit ?? 'g'} onValueChange={(v) => { if (v) draft.unit = v; }} variant="outline" size="sm" class="w-full">
				{#each FOOD_UNITS as u (u)}
					<ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>
	</div>

	<Separator class="my-4" />

	<!-- Section 2: GI, Fat, Protein, Energy -->
	<div class="grid gap-4" style="grid-template-columns: 1.4fr 1fr 1fr 1fr">
		<!-- GI -->
		<div class="flex flex-col gap-1.5">
			<span class="text-muted-foreground font-semibold" style="font-size: 11px">Glycemic Index</span>
			<ToggleGroup.Root type="single" value={giFromInt(draft.gi)} onValueChange={(v) => { if (v) draft.gi = giToInt(v as GiLevel); }} variant="outline" size="sm" class="w-full">
				{#each giLevels as g (g)}
					<ToggleGroup.Item value={g} class="flex-1 capitalize gap-1.5">
						<GiIcon level={g} size={7} />{g}
					</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>

		<!-- Fat -->
		<div class="flex flex-col gap-1.5">
			<span class="font-semibold" style="font-size: 11px">
				<span class="text-muted-foreground">Fat</span>
				<span class="ml-1" style="font-size: 10px; color: oklch(1 0 0 / 0.3)">optional</span>
			</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.fat}
					min="0"
					step="0.1"
				/>
				<span class="ml-2 shrink-0 text-xs text-muted-foreground">g</span>
			</div>
		</div>

		<!-- Protein -->
		<div class="flex flex-col gap-1.5">
			<span class="font-semibold" style="font-size: 11px">
				<span class="text-muted-foreground">Protein</span>
				<span class="ml-1" style="font-size: 10px; color: oklch(1 0 0 / 0.3)">optional</span>
			</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.protein}
					min="0"
					step="0.1"
				/>
				<span class="ml-2 shrink-0 text-xs text-muted-foreground">g</span>
			</div>
		</div>

		<!-- Energy -->
		<div class="flex flex-col gap-1.5">
			<span class="font-semibold" style="font-size: 11px">
				<span class="text-muted-foreground">Energy</span>
				<span class="ml-1" style="font-size: 10px; color: oklch(1 0 0 / 0.3)">auto</span>
			</span>
			<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.energy}
					min="0"
					step="1"
				/>
				<span class="ml-2 shrink-0 text-xs text-muted-foreground">kcal</span>
			</div>
		</div>
	</div>

	<Separator class="my-4" />

	<!-- Section 3: Category, Subcategory, Actions -->
	<div class="flex items-center justify-between">
		<div class="flex items-center gap-3">
			<!-- Category -->
			<Select.Root type="single" value={draft.category ?? ''} onValueChange={(v) => { draft.category = v; }}>
				<Select.Trigger class="h-9 w-45">
					{draft.category || 'No category'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No category" />
					{#each foodState.categories as cat (cat)}
						<Select.Item value={cat} label={cat} />
					{/each}
				</Select.Content>
			</Select.Root>

			<!-- Subcategory -->
			<Select.Root type="single" value={draft.subcategory ?? ''} onValueChange={(v) => { draft.subcategory = v; }}>
				<Select.Trigger class="h-9 w-45">
					{draft.subcategory || 'No subcategory'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="No subcategory" />
					{#each subcategories as sub (sub)}
						<Select.Item value={sub} label={sub} />
					{/each}
				</Select.Content>
			</Select.Root>
		</div>

		<div class="flex items-center gap-2">
			<Button variant="ghost" size="sm" class="text-destructive hover:text-destructive" onclick={handleDeleteClick}><Trash2 class="h-3.5 w-3.5" /> Delete</Button>
			<Button variant="outline" size="sm" onclick={oncancel}>Cancel</Button>
			<Button size="sm" onclick={() => onsave(draft)}><Check class="h-3.5 w-3.5" /> Save changes</Button>
		</div>
	</div>
</div>
