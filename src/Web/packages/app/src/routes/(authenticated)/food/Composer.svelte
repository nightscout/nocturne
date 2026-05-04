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

	function handleKeydown(e: KeyboardEvent) {
		const mod = e.metaKey || e.ctrlKey;
		if (mod && e.key === 'Enter') {
			e.preventDefault();
			submit(true);
		} else if (e.shiftKey && e.key === 'Enter') {
			e.preventDefault();
			submit(false);
		} else if (e.key === 'Escape') {
			e.preventDefault();
			onclose();
		}
	}
</script>

<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	class="mx-4 my-3 rounded-[10px] p-3.5"
	style="border: 1px solid var(--carbs-border); background: var(--carbs-bg-subtle)"
	onkeydown={handleKeydown}
>
	<!-- Header -->
	<div class="mb-3 flex items-center gap-3">
		<div class="flex items-center justify-center rounded-[7px]" style="width: 26px; height: 26px; background: var(--carbs-soft)">
			<Plus size={14} style="color: var(--carbs)" />
		</div>
		<span class="font-semibold" style="font-size: 13px">Add food</span>
		<span class="text-muted-foreground" style="font-size: 11px">
			Tab through fields · ⌘+Enter to save and add another · Esc to close
		</span>
		<Button variant="ghost" size="icon" class="ml-auto h-8 w-8" onclick={onclose}><X class="h-3.5 w-3.5" /></Button>
	</div>

	<!-- Single-row form -->
	<div class="grid items-end gap-3" style="grid-template-columns: 1.6fr 110px 90px 1fr 1.4fr; height: 42px">
		<!-- Name -->
		<div class="flex h-full flex-col gap-1">
			<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Name</span>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="text"
					class="w-full bg-transparent text-sm outline-none"
					placeholder="e.g. Greek yogurt, plain"
					bind:this={nameInput}
					bind:value={draft.name}
				/>
			</div>
		</div>

		<!-- Carbs -->
		<div class="flex h-full flex-col gap-1">
			<span class="font-medium uppercase" style="font-size: 10px; color: var(--carbs)">Carbs</span>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid var(--carbs-border-strong); background: var(--carbs-bg)">
				<input
					type="number"
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
			<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Per</span>
			<div class="flex flex-1 items-center rounded-md px-3" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
				<input
					type="number"
					class="w-full bg-transparent text-sm outline-none"
					bind:value={draft.portion}
					min="0"
					step="1"
				/>
			</div>
		</div>

		<!-- Unit -->
		<div class="flex h-full flex-col gap-1">
			<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Unit</span>
			<ToggleGroup.Root type="single" value={draft.unit ?? 'g'} onValueChange={(v) => { if (v) draft = { ...draft, unit: v }; }} variant="outline" size="sm" class="w-full flex-1">
				{#each FOOD_UNITS as u (u)}
					<ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
				{/each}
			</ToggleGroup.Root>
		</div>

		<!-- GI -->
		<div class="flex h-full flex-col gap-1">
			<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">GI</span>
			<ToggleGroup.Root type="single" value={giFromInt(draft.gi)} onValueChange={(v) => { if (v) draft = { ...draft, gi: giToInt(v as GiLevel) }; }} variant="outline" size="sm" class="w-full flex-1">
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
					<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Fat</span>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
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
					<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Protein</span>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
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
					<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Energy</span>
					<div class="flex items-center rounded-md px-3 py-2" style="border: 1px solid oklch(1 0 0 / 0.18); background: oklch(1 0 0 / 0.04)">
						<input
							type="number"
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
					<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Category</span>
					<Select.Root type="single" value={draft.category ?? ''} onValueChange={(v) => { draft = { ...draft, category: v }; }}>
						<Select.Trigger class="h-9 w-full text-xs">
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
					<span class="text-muted-foreground font-medium uppercase" style="font-size: 10px">Subcategory</span>
					<Select.Root type="single" value={draft.subcategory ?? ''} onValueChange={(v) => { draft = { ...draft, subcategory: v }; }}>
						<Select.Trigger class="h-9 w-full text-xs">
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
			<Button variant="outline" size="sm" disabled={!canSave} onclick={() => submit(false)}>Save</Button>
			<Button size="sm" disabled={!canSave} onclick={() => submit(true)}>Save & add another <span class="ml-1 text-[11px] opacity-60">⌘+Enter</span></Button>
		</div>
	</div>
</div>
