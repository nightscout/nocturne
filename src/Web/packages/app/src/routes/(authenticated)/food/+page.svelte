<script lang="ts">
  import { FoodState } from './food-state.svelte.js';
  import { setFoodState } from './food-context.js';
  import { onMount } from 'svelte';
  import type { Food } from '$api';
  import type { GiLevel } from './types';

  import FoodList from './FoodList.svelte';
  import Composer from './Composer.svelte';
  import GiIcon from './GiIcon.svelte';

  import { Plus, Download, Upload, Search, Star, X, Apple } from 'lucide-svelte';
  import * as Select from '$lib/components/ui/select';
  import { Button } from '$lib/components/ui/button';
  import { Separator } from '$lib/components/ui/separator';
  import * as InputGroup from '$lib/components/ui/input-group';
  import { Toggle } from '$lib/components/ui/toggle';
  import * as ToggleGroup from '$lib/components/ui/toggle-group';
  import { isGiLevel } from './types';

  const state = new FoodState();
  setFoodState(state);

  // `.run()` rejects during the render flush, so defer the bootstrap to a microtask.
  onMount(() => queueMicrotask(() => state.load()));

  const giLevels: GiLevel[] = ['low', 'medium', 'high'];
  const ALL_CATEGORIES = '__all';

  async function handleAdd(food: Food) {
    await state.addFood(food);
  }
</script>

<svelte:head>
  <title>Food Editor - Nocturne</title>
</svelte:head>

<div class="food-editor @container flex-1 overflow-auto p-5">
  <!-- Header -->
  <div class="mb-5 flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Food Editor</h1>
      <div class="mt-0.5 text-xs text-muted-foreground">
        {#if state.foods.length === 0 && !state.loading}
          No foods yet — add one to get started
        {:else}
          {state.foods.length} foods
        {/if}
      </div>
    </div>
    <div class="flex flex-wrap gap-2">
      <Button variant="outline" size="sm"><Download class="h-3.5 w-3.5" /> Export</Button>
      <Button variant="outline" size="sm"><Upload class="h-3.5 w-3.5" /> Import CSV</Button>
      <Button size="sm" onclick={() => (state.composerOpen = true)}><Plus class="h-3.5 w-3.5" /> Add food</Button>
    </div>
  </div>

  <!-- Main card -->
  <div class="overflow-hidden rounded-xl border border-border bg-card">
    <!-- Toolbar -->
    <div class="flex flex-wrap items-center gap-2.5 border-b border-border bg-card px-4 py-3.5">
      <InputGroup.Root data-testid="food-search" class="min-w-[180px] flex-1">
        <InputGroup.Addon>
          <Search />
        </InputGroup.Addon>
        <InputGroup.Input
          placeholder="Search {state.foods.length} foods..."
          bind:value={state.query}
        />
        {#if state.query}
          <InputGroup.Addon align="inline-end">
            <Button variant="ghost" size="icon-xs" aria-label="Clear search" onclick={() => (state.query = '')}><X class="h-3 w-3" /></Button>
          </InputGroup.Addon>
        {/if}
      </InputGroup.Root>

      <Toggle
        data-testid="food-favorites-filter"
        variant="outline"
        size="sm"
        bind:pressed={state.favoritesOnly}
      >
        <Star class="h-3 w-3" /> Favorites
      </Toggle>

      <Separator orientation="vertical" class="h-5" />

      <Select.Root type="single" bind:value={state.sort}>
        <Select.Trigger data-testid="food-sort" size="sm">
          {state.sort === 'name' ? 'Sort: A → Z' : state.sort === 'carbs' ? 'Sort: Carbs (high)' : 'Sort: Recently added'}
        </Select.Trigger>
        <Select.Content>
          <Select.Item value="name" label="Sort: A → Z" />
          <Select.Item value="carbs" label="Sort: Carbs (high)" />
          <Select.Item value="recent" label="Sort: Recently added" />
        </Select.Content>
      </Select.Root>
    </div>

    <!-- Filter chips -->
    <div class="flex flex-wrap items-center gap-1.5 border-b border-border px-4 py-2.5">
      <span class="mr-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground/60">Category</span>
      <ToggleGroup.Root
        type="single"
        variant="outline"
        size="xs"
        spacing={1}
        class="flex-wrap"
        value={state.categoryFilter ?? ALL_CATEGORIES}
        onValueChange={(v: string) => (state.categoryFilter = v && v !== ALL_CATEGORIES ? v : null)}
      >
        <ToggleGroup.Item value={ALL_CATEGORIES}>All</ToggleGroup.Item>
        {#each state.categories as cat (cat)}
          <ToggleGroup.Item value={cat}>{cat}</ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>

      <Separator orientation="vertical" class="mx-1 h-4" />

      <span class="mr-1 text-xs font-semibold uppercase tracking-wider text-muted-foreground/60">GI</span>
      <ToggleGroup.Root
        type="single"
        variant="outline"
        size="xs"
        spacing={1}
        value={state.giFilter ?? ''}
        onValueChange={(v: string) => (state.giFilter = isGiLevel(v) ? v : null)}
      >
        {#each giLevels as g (g)}
          <ToggleGroup.Item value={g}>
            <GiIcon level={g} size={7} />
            <span class="capitalize">{g}</span>
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>

      <span class="ml-auto text-xs text-muted-foreground">
        {state.filteredFoods.length} of {state.foods.length}
      </span>
    </div>

    <!-- Composer -->
    {#if state.composerOpen}
      <Composer onadd={handleAdd} onclose={() => (state.composerOpen = false)} />
    {/if}

    <!-- Content -->
    {#if state.loading}
      <div class="py-16 text-center text-muted-foreground">Loading food database...</div>
    {:else if state.foods.length === 0}
      <div class="flex flex-col items-center justify-center gap-3 px-6 py-16 text-center text-muted-foreground">
        <div class="grid h-14 w-14 place-items-center rounded-2xl bg-carbs/12 text-(--carbs-strong)">
          <Apple class="h-7 w-7" />
        </div>
        <div class="text-lg font-semibold text-foreground">Build your food database</div>
        <div class="max-w-[380px] text-sm leading-relaxed">
          Add the foods you eat regularly with their carb counts. Once they're here,
          logging a meal takes a couple of taps anywhere in Nocturne.
        </div>
        <div class="mt-1.5 flex gap-2">
          <Button size="sm" onclick={() => (state.composerOpen = true)}><Plus class="h-3.5 w-3.5" /> Add your first food</Button>
          <Button variant="outline" size="sm"><Upload class="h-3.5 w-3.5" /> Import CSV</Button>
        </div>
        <div class="mt-4 text-xs text-muted-foreground/60">
          Or browse the Nocturne food bank →
        </div>
      </div>
    {:else if state.filteredFoods.length === 0}
      <div class="flex flex-col items-center justify-center gap-3 px-6 py-16 text-center">
        <div class="grid h-14 w-14 place-items-center rounded-2xl bg-white/[0.05] text-muted-foreground">
          <Search class="h-6 w-6" />
        </div>
        <div class="text-base font-semibold text-foreground">No matches for "{state.query}"</div>
        <Button variant="outline" size="sm" onclick={() => state.clearFilters()}>Clear filters</Button>
      </div>
    {:else}
      <FoodList />
    {/if}
  </div>
</div>

<style>
  .food-editor {
    --carbs-strong: color-mix(in oklch, var(--carbs), white 15%);
  }
</style>
