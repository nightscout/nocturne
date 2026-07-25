<script lang="ts">
	import type { Food } from '$api';
	import { Plus, X, ChevronRight } from 'lucide-svelte';
	import GiIcon from './GiIcon.svelte';
	import { getFoodState } from './food-context.js';
	import { giFromInt, giToInt } from './types.js';
	import type { GiLevel } from './types.js';
	import { FOOD_UNITS, DEFAULT_PORTION, DEFAULT_GI } from '$lib/components/food';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import * as ToggleGroup from '$lib/components/ui/toggle-group';
	import * as Collapsible from '$lib/components/ui/collapsible';

	interface Props {
		onadd: (food: Food) => void;
		onclose: () => void;
	}

	const { onadd, onclose }: Props = $props();
	const foodState = getFoodState();

	const giLevels: GiLevel[] = ['low', 'medium', 'high'];

	function emptyDraft(): Food {
		return {
			name: undefined,
			carbs: undefined,
			portion: DEFAULT_PORTION,
			unit: 'g',
			gi: DEFAULT_GI,
			type: 'food',
			fat: undefined,
			protein: undefined,
			energy: undefined,
			category: undefined,
			subcategory: undefined,
		};
	}

	let draft = $state<Food>(emptyDraft());
	let showDetails = $state(false);
	let nameInput: HTMLInputElement | undefined = $state();

	const canSave = $derived(!!draft.name && draft.carbs !== undefined && !!draft.portion);

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

	$effect(() => {
		nameInput?.focus();
	});

	function submit(addAnother: boolean) {
		if (!canSave) return;
		onadd(draft);
		if (addAnother) {
			const keepPortion = draft.portion;
			const keepUnit = draft.unit;
			const keepGi = draft.gi;
			const keepCategory = draft.category;
			draft = emptyDraft();
			draft.portion = keepPortion;
			draft.unit = keepUnit;
			draft.gi = keepGi;
			draft.category = keepCategory;
			nameInput?.focus();
		} else {
			onclose();
		}
	}

	/** Enter saves and closes, after the browser's required-field checks. */
	function handleSubmit(e: SubmitEvent) {
		e.preventDefault();
		submit(false);
	}

	function handleKeydown(e: KeyboardEvent) {
		const mod = e.metaKey || e.ctrlKey;
		if (mod && e.key === 'Enter') {
			e.preventDefault();
			submit(true);
		} else if (e.key === 'Escape') {
			e.preventDefault();
			onclose();
		}
	}
</script>

<!-- The keydown handler carries the composer's own shortcuts (save and add
     another, close) — submit and required-field checks come from the form. -->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<form
	class="mx-4 my-3 rounded-[10px] p-3.5"
	style="border: 1px solid var(--carbs-border); background: var(--carbs-bg-subtle)"
	onsubmit={handleSubmit}
	onkeydown={handleKeydown}
>
	<!-- Header -->
	<div class="mb-3 flex items-center gap-3">
		<div class="flex items-center justify-center rounded-[7px]" style="width: 26px; height: 26px; background: var(--carbs-soft)">
			<Plus size={14} style="color: var(--carbs)" />
		</div>
		<span class="font-semibold" style="font-size: 13px">Add food</span>
		<span class="text-muted-foreground" style="font-size: 11px">
			Tab through fields · Enter to save · ⌘+Enter to save and add another · Esc to close
		</span>
		<Button type="button" variant="ghost" size="icon" class="ml-auto h-8 w-8" onclick={onclose}><X class="h-3.5 w-3.5" /></Button>
	</div>

	<!-- Single-row form -->
	<div class="grid items-end gap-3" style="grid-template-columns: 1.6fr 110px 90px 1fr 1.4fr; height: 42px">
		<!-- Name -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-name" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Name</label>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					id="composer-name"
					name="name"
					type="text"
					required
					class="w-full bg-transparent text-sm outline-none"
					placeholder="e.g. Greek yogurt, plain"
					bind:this={nameInput}
					bind:value={draft.name}
				/>
			</div>
		</div>

		<!-- Carbs -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-carbs" class="font-medium uppercase" style="font-size: 10px; color: var(--carbs)">Carbs</label>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid var(--carbs-border-strong); background: var(--carbs-bg)">
				<input
					id="composer-carbs"
					name="carbs"
					type="number"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.carbs}
					min="0"
					step="0.1"
				/>
				<span class="ml-1 shrink-0 text-xs" style="color: var(--carbs)">g</span>
			</div>
		</div>

		<!-- Per (portion) -->
		<div class="flex h-full flex-col gap-1">
			<label for="composer-portion" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Per</label>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					id="composer-portion"
					name="portion"
					type="number"
					required
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.portion}
					min="0"
					step="1"
				/>
			</div>
		</div>

		<!-- Unit -->
		<div class="flex h-full flex-col gap-1">
			<span id="composer-unit-label" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Unit</span>
			<ToggleGroup.Root aria-labelledby="composer-unit-label" type="single" value={draft.unit ?? 'g'} onValueChange={(v: string) => { if (v) draft = { ...draft, unit: v }; }} variant="outline" size="sm" class="w-full flex-1">
				{#each FOOD_UNITS as u (u)}
					<ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>

		<!-- GI -->
		<div class="flex h-full flex-col gap-1">
			<span id="composer-gi-label" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">GI</span>
			<ToggleGroup.Root aria-labelledby="composer-gi-label" type="single" value={giFromInt(draft.gi)} onValueChange={(v: string) => { if (v) draft = { ...draft, gi: giToInt(v as GiLevel) }; }} variant="outline" size="sm" class="w-full flex-1">
				{#each giLevels as g (g)}
					<ToggleGroup.Item value={g} class="flex-1 capitalize gap-1.5">
						<GiIcon level={g} size={7} />{g}
					</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>
	</div>

	<!-- Footer -->
	<div class="mt-3 flex items-center justify-between">
		<!-- Details toggle -->
		<Collapsible.Root bind:open={showDetails}>
			<Collapsible.Trigger class="inline-flex cursor-pointer select-none items-center gap-1.5 text-xs text-muted-foreground">
				<span class="inline-flex transition-transform" style:transform={showDetails ? 'rotate(90deg)' : ''}><ChevronRight class="h-3 w-3" /></span> {showDetails ? 'Hide' : 'Add'} fat, protein, category...
			</Collapsible.Trigger>
			<Collapsible.Content>
			<div class="mt-3 grid gap-3" style="grid-template-columns: 1fr 1fr 1fr 1fr 1fr">
				<!-- Fat -->
				<div class="flex flex-col gap-1">
					<label for="composer-fat" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Fat</label>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
							id="composer-fat"
							name="fat"
							class="w-full bg-transparent text-sm outline-none"
							bind:value={draft.fat}
							min="0"
							step="0.1"
						/>
						<span class="ml-1 shrink-0 text-xs text-muted-foreground">g</span>
					</div>
				</div>

				<!-- Protein -->
				<div class="flex flex-col gap-1">
					<label for="composer-protein" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Protein</label>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
							id="composer-protein"
							name="protein"
							class="w-full bg-transparent text-sm outline-none"
							bind:value={draft.protein}
							min="0"
							step="0.1"
						/>
						<span class="ml-1 shrink-0 text-xs text-muted-foreground">g</span>
					</div>
				</div>

				<!-- Energy -->
				<div class="flex flex-col gap-1">
					<label for="composer-energy" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Energy</label>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
							id="composer-energy"
							name="energy"
							class="w-full bg-transparent text-sm outline-none"
							bind:value={draft.energy}
							min="0"
							step="1"
						/>
						<span class="ml-1 shrink-0 text-xs text-muted-foreground">kcal</span>
					</div>
				</div>

				<!-- Category -->
				<div class="flex flex-col gap-1">
					<label for="composer-category" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Category</label>
					<Select.Root type="single" name="category" value={draft.category ?? ''} onValueChange={(v) => { draft = { ...draft, category: v }; }}>
						<Select.Trigger id="composer-category" class="h-9 w-full text-xs">
							{draft.category || 'Category'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="None" />
							{#each foodState.categories as cat (cat)}
								<Select.Item value={cat} label={cat} />
							{/each}
						</Select.Content>
					</Select.Root>
				</div>

				<!-- Subcategory -->
				<div class="flex flex-col gap-1">
					<label for="composer-subcategory" class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Subcategory</label>
					<Select.Root type="single" name="subcategory" value={draft.subcategory ?? ''} onValueChange={(v) => { draft = { ...draft, subcategory: v }; }}>
						<Select.Trigger id="composer-subcategory" class="h-9 w-full text-xs">
							{draft.subcategory || 'Subcategory'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="" label="None" />
							{#each subcategories as sub (sub)}
								<Select.Item value={sub} label={sub} />
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>
			</Collapsible.Content>
		</Collapsible.Root>

		<!-- Action buttons -->
		<div class="flex items-center gap-2">
			<Button type="submit" variant="outline" size="sm" disabled={!canSave}>Save</Button>
			<Button type="button" size="sm" disabled={!canSave} onclick={() => submit(true)}>Save & add another <span class="ml-1 text-[11px] opacity-60">⌘+Enter</span></Button>
		</div>
	</div>
</form>
