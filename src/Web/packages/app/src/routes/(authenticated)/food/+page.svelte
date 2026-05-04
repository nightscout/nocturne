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

  const state = new FoodState();
  setFoodState(state);

  onMount(() => state.load());

  const giLevels: GiLevel[] = ['low', 'medium', 'high'];
  const giLabels: Record<GiLevel, string> = { low: 'Low', medium: 'Med', high: 'High' };

  async function handleAdd(food: Food) {
    await state.addFood(food);
  }
</script>

<svelte:head>
  <title>Food Editor - Nocturne</title>
</svelte:head>

<div class="food-editor flex-1 overflow-auto p-5">
  <!-- Header -->
  <div class="mb-5 flex items-center justify-between">
    <div>
      <h1 class="text-[22px] font-bold tracking-tight">Food Editor</h1>
      <div class="mt-0.5 text-xs text-muted-foreground">
        {#if state.foods.length === 0 && !state.loading}
          No foods yet — add one to get started
        {:else}
          {state.foods.length} foods
        {/if}
      </div>
    </div>
    <div class="flex gap-2">
      <Button variant="outline" size="sm"><Download class="h-3.5 w-3.5" /> Export</Button>
      <Button variant="outline" size="sm"><Upload class="h-3.5 w-3.5" /> Import CSV</Button>
      <Button size="sm" onclick={() => (state.composerOpen = true)}><Plus class="h-3.5 w-3.5" /> Add food</Button>
    </div>
  </div>

  <!-- Main card -->
  <div class="overflow-hidden rounded-[14px] border border-border bg-card">
    <!-- Toolbar -->
    <div class="flex items-center gap-2.5 border-b border-border bg-card px-4 py-3.5">
      <div class="flex h-[38px] flex-1 items-center gap-2 rounded-lg border border-border bg-white/[0.04] px-2.5 text-[13px] focus-within:border-ring focus-within:bg-white/[0.07]">
        <Search class="h-[15px] w-[15px] text-muted-foreground" />
        <input
          class="h-full flex-1 border-0 bg-transparent p-0 text-foreground outline-0"
          placeholder="Search {state.foods.length} foods..."
          bind:value={state.query}
        />
        {#if state.query}
          <Button variant="ghost" size="icon" class="h-7 w-7" aria-label="Clear search" onclick={() => (state.query = '')}><X class="h-3 w-3" /></Button>
        {/if}
      </div>

      <button
        class="flex h-8 items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 text-xs cursor-pointer transition-all"
        style:background={state.favoritesOnly ? 'oklch(1 0 0 / 0.08)' : 'transparent'}
        style:border-color={state.favoritesOnly ? 'oklch(1 0 0 / 0.16)' : undefined}
        class:text-foreground={state.favoritesOnly}
        class:text-muted-foreground={!state.favoritesOnly}
        class:border-border={!state.favoritesOnly}
        onclick={() => (state.favoritesOnly = !state.favoritesOnly)}
      >
        <Star class="h-3 w-3" /> Favorites
      </button>

      <Separator orientation="vertical" class="h-5" />

      <Select.Root type="single" bind:value={state.sort}>
        <Select.Trigger class="h-8 rounded-lg border border-border bg-white/4 px-2.5 text-xs">
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
      <span class="mr-1.5 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground/60">Category</span>
      <button
        class="flex h-7 items-center rounded-full border px-2.5 text-xs cursor-pointer transition-all"
        style:background={state.categoryFilter === null ? 'oklch(1 0 0 / 0.08)' : 'transparent'}
        style:border-color={state.categoryFilter === null ? 'oklch(1 0 0 / 0.16)' : undefined}
        class:text-foreground={state.categoryFilter === null}
        class:text-muted-foreground={state.categoryFilter !== null}
        class:border-border={state.categoryFilter !== null}
        onclick={() => (state.categoryFilter = null)}
      >All</button>
      {#each state.categories as cat (cat)}
        <button
          class="flex h-7 items-center rounded-full border px-2.5 text-xs cursor-pointer transition-all"
          style:background={state.categoryFilter === cat ? 'oklch(1 0 0 / 0.08)' : 'transparent'}
          style:border-color={state.categoryFilter === cat ? 'oklch(1 0 0 / 0.16)' : undefined}
          class:text-foreground={state.categoryFilter === cat}
          class:text-muted-foreground={state.categoryFilter !== cat}
          class:border-border={state.categoryFilter !== cat}
          onclick={() => (state.categoryFilter = state.categoryFilter === cat ? null : cat)}
        >{cat}</button>
      {/each}

      <Separator orientation="vertical" class="mx-1 h-4" />

      <span class="mr-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground/60">GI</span>
      {#each giLevels as g (g)}
        <button
          class="flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-xs cursor-pointer transition-all"
          style:background={state.giFilter === g ? 'oklch(1 0 0 / 0.08)' : 'transparent'}
          style:border-color={state.giFilter === g ? 'oklch(1 0 0 / 0.16)' : undefined}
          class:text-foreground={state.giFilter === g}
          class:text-muted-foreground={state.giFilter !== g}
          class:border-border={state.giFilter !== g}
          onclick={() => (state.giFilter = state.giFilter === g ? null : g)}
        >
          <GiIcon level={g} size={7} />
          <span class="capitalize">{g}</span>
        </button>
      {/each}

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
        <div class="grid h-14 w-14 place-items-center rounded-2xl" style="background: var(--carbs-soft); color: var(--carbs-strong);">
          <Apple class="h-7 w-7" />
        </div>
        <div class="text-lg font-semibold text-foreground">Build your food database</div>
        <div class="max-w-[380px] text-[13px] leading-relaxed">
          Add the foods you eat regularly with their carb counts. Once they're here,
          logging a meal takes a couple of taps anywhere in Nocturne.
        </div>
        <div class="mt-1.5 flex gap-2">
          <Button size="sm" onclick={() => (state.composerOpen = true)}><Plus class="h-3.5 w-3.5" /> Add your first food</Button>
          <Button variant="outline" size="sm"><Upload class="h-3.5 w-3.5" /> Import CSV</Button>
        </div>
        <div class="mt-4 text-[11px] text-muted-foreground/60">
          Or browse the Nocturne food bank →
        </div>
      </div>
    {:else if state.filteredFoods.length === 0}
      <div class="flex flex-col items-center justify-center gap-3 px-6 py-16 text-center">
        <div class="grid h-14 w-14 place-items-center rounded-2xl bg-white/[0.05] text-muted-foreground">
          <Search class="h-6 w-6" />
        </div>
        <div class="text-[15px] font-semibold text-foreground">No matches for "{state.query}"</div>
        <Button variant="outline" size="sm" onclick={() => state.clearFilters()}>Clear filters</Button>
      </div>
    {:else}
      <FoodList />
    {/if}
  </div>
</div>

<style>
  .food-editor {
    --carbs-soft: color-mix(in oklch, var(--carbs) 12%, transparent);
    --carbs-strong: color-mix(in oklch, var(--carbs), white 15%);
    --carbs-border: color-mix(in oklch, var(--carbs) 22%, transparent);
    --carbs-border-strong: color-mix(in oklch, var(--carbs) 45%, transparent);
    --carbs-bg: color-mix(in oklch, var(--carbs) 6%, transparent);
    --carbs-bg-subtle: color-mix(in oklch, var(--carbs) 4%, transparent);
  }
</style>
