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
	import { Input } from '$lib/components/ui/input';
	import { Label } from '$lib/components/ui/label';
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

	/** Enter saves, after the browser's required-field checks. */
	function handleSubmit(e: SubmitEvent) {
		e.preventDefault();
		onsave(draft);
	}
</script>

<form
	class="border-y border-border bg-background/60 px-4 py-4"
	onsubmit={handleSubmit}
>
	<!-- Delete confirmation bar -->
	{#if confirming}
		<div class="mb-4 flex items-center gap-3 rounded-lg border border-destructive/30 bg-destructive/15 px-4 py-3">
			<Trash2 size={16} class="shrink-0 text-destructive" />
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
	<div class="grid grid-cols-[1.6fr_1fr_1fr_1fr] gap-4">
		<!-- Name -->
		<div class="flex flex-col gap-1.5">
			<label for="food-edit-name" class="text-muted-foreground font-semibold text-xs">
				Name <span aria-hidden="true">*</span><span class="sr-only">(required)</span>
			</label>
			<div class="flex items-center rounded-md px-3 py-2 border border-input dark:bg-input/30">
				<input
					id="food-edit-name"
					name="name"
					type="text"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.name}
				/>
			</div>
		</div>

		<!-- Carbs -->
		<div class="flex flex-col gap-1.5">
			<label for="food-edit-carbs" class="font-semibold text-xs text-carbs">
				Carbs <span aria-hidden="true">*</span><span class="sr-only">(required)</span>
			</label>
			<div class="flex items-center rounded-md px-3 py-2 border border-carbs/45 bg-carbs/6">
				<input
					id="food-edit-carbs"
					name="carbs"
					type="number"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.carbs}
					min="0"
					step="0.1"
				/>
				<span class="ml-2 shrink-0 text-xs text-carbs">g</span>
			</div>
			<span class="text-muted-foreground text-2xs">per {draft.portion ?? 100} {draft.unit ?? 'g'}</span>
		</div>

		<!-- Portion -->
		<div class="flex flex-col gap-1.5">
			<label for="food-edit-portion" class="text-muted-foreground font-semibold text-xs">
				Portion <span aria-hidden="true">*</span><span class="sr-only">(required)</span>
			</label>
			<div class="flex items-center rounded-md px-3 py-2 border border-input dark:bg-input/30">
				<input
					id="food-edit-portion"
					name="portion"
					type="number"
					required
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
			<span id="food-edit-unit-label" class="text-muted-foreground font-semibold text-xs">Unit</span>
			<ToggleGroup.Root aria-labelledby="food-edit-unit-label" type="single" value={draft.unit ?? 'g'} onValueChange={(v: string) => { if (v) draft.unit = v; }} variant="outline" size="sm" class="w-full">
				{#each FOOD_UNITS as u (u)}
					<ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>
	</div>

	<Separator class="my-4" />

	<!-- Section 2: GI, Fat, Protein, Energy -->
	<div class="grid grid-cols-[1.4fr_1fr_1fr_1fr] gap-4">
		<!-- GI -->
		<div class="flex flex-col gap-1.5">
			<span id="food-edit-gi-label" class="text-muted-foreground font-semibold text-xs">Glycemic Index</span>
			<ToggleGroup.Root aria-labelledby="food-edit-gi-label" type="single" value={giFromInt(draft.gi)} onValueChange={(v: string) => { if (v) draft.gi = giToInt(v as GiLevel); }} variant="outline" size="sm" class="w-full">
				{#each giLevels as g (g)}
					<ToggleGroup.Item value={g} class="capitalize">
						<GiIcon level={g} size={7} />{g}
					</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>

		<!-- Fat -->
		<div class="flex flex-col gap-1.5">
			<Label for="food-edit-fat" size="sm" variant="muted">
				Fat
				<span class="text-2xs font-normal text-foreground/30">optional</span>
			</Label>
			<div class="flex items-center gap-2">
				<Input
					type="number"
					id="food-edit-fat"
					name="fat"
					bind:value={draft.fat}
					min="0"
					step="0.1"
				/>
				<span class="shrink-0 text-xs text-muted-foreground">g</span>
			</div>
		</div>

		<!-- Protein -->
		<div class="flex flex-col gap-1.5">
			<Label for="food-edit-protein" size="sm" variant="muted">
				Protein
				<span class="text-2xs font-normal text-foreground/30">optional</span>
			</Label>
			<div class="flex items-center gap-2">
				<Input
					type="number"
					id="food-edit-protein"
					name="protein"
					bind:value={draft.protein}
					min="0"
					step="0.1"
				/>
				<span class="shrink-0 text-xs text-muted-foreground">g</span>
			</div>
		</div>

		<!-- Energy -->
		<div class="flex flex-col gap-1.5">
			<Label for="food-edit-energy" size="sm" variant="muted">
				Energy
				<span class="text-2xs font-normal text-foreground/30">auto</span>
			</Label>
			<div class="flex items-center gap-2">
				<Input
					type="number"
					id="food-edit-energy"
					name="energy"
					bind:value={draft.energy}
					min="0"
					step="1"
				/>
				<span class="shrink-0 text-xs text-muted-foreground">kcal</span>
			</div>
		</div>
	</div>

	<Separator class="my-4" />

	<!-- Section 3: Category, Subcategory, Actions -->
	<div class="flex items-center justify-between">
		<div class="flex items-center gap-3">
			<!-- Category -->
			<Select.Root type="single" name="category" value={draft.category ?? ''} onValueChange={(v) => { draft.category = v; }}>
				<Select.Trigger aria-label="Category" class="w-45">
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
			<Select.Root type="single" name="subcategory" value={draft.subcategory ?? ''} onValueChange={(v) => { draft.subcategory = v; }}>
				<Select.Trigger aria-label="Subcategory" class="w-45">
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
			<Button variant="ghost-destructive" size="sm" onclick={handleDeleteClick}><Trash2 class="h-3.5 w-3.5" /> Delete</Button>
			<Button variant="outline" size="sm" onclick={oncancel}>Cancel</Button>
			<Button type="submit" size="sm"><Check class="h-3.5 w-3.5" /> Save changes</Button>
		</div>
	</div>
</form>
