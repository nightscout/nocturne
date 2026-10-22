<script lang="ts">
  import type { Food } from "$api";
  import type { Snippet } from "svelte";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import {
    energyFromDisplayUnits,
    energyToDisplayUnits,
    type EnergyUnit,
  } from "@nocturne/ui/energy";
  import { getEnergyUnit } from "$lib/stores/appearance-store.svelte";
  import GiIcon from "./GiIcon.svelte";
  import GiLabel from "./GiLabel.svelte";
  import CategorySubcategoryCombobox from "./CategorySubcategoryCombobox.svelte";
  import { giFromInt, giToInt, isGiLevel, type GiLevel } from "./gi";
  import { FOOD_UNITS, type FoodCategoryMap } from "./food-constants";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as InputGroup from "$lib/components/ui/input-group";
  import { Label } from "$lib/components/ui/label";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import * as Collapsible from "$lib/components/ui/collapsible";

  interface Props {
    draft: Food;
    categories: FoodCategoryMap;
    onCategoryCreate?: (category: string) => void;
    onSubcategoryCreate?: (category: string, subcategory: string) => void;
    nameRef?: HTMLInputElement | null;
    collapseDetails?: boolean;
    actions?: Snippet;
  }

  let {
    draft = $bindable(),
    categories,
    onCategoryCreate,
    onSubcategoryCreate,
    nameRef = $bindable(null),
    collapseDetails = false,
    actions,
  }: Props = $props();

  const uid = $props.id();
  const giLevels: GiLevel[] = ["low", "medium", "high"];
  const energyUnit = $derived(getEnergyUnit());

  let showDetails = $state(false);

  function displayedEnergy(): number | undefined {
    return draft.energy == null ? undefined : energyToDisplayUnits(draft.energy, energyUnit);
  }

  /**
   * The field holds what was typed and writes kilojoules only on an edit, so typing is never
   * rounded under the cursor and an untouched value saves exactly as stored. It is reformatted from
   * the stored value on blur, or when the value or unit changes from outside.
   */
  let energyInput = $state(displayedEnergy());
  let writtenEnergy: number | undefined;
  let shownUnit: EnergyUnit | undefined;

  $effect(() => {
    if (draft.energy === writtenEnergy && energyUnit === shownUnit) return;
    writtenEnergy = draft.energy;
    shownUnit = energyUnit;
    energyInput = displayedEnergy();
  });

  function setEnergy(value: number | null | undefined) {
    energyInput = typeof value === "number" ? value : undefined;
    draft.energy = typeof value === "number" ? energyFromDisplayUnits(value, energyUnit) : undefined;
    writtenEnergy = draft.energy;
  }
</script>

{#snippet required()}
  <span aria-hidden="true">*</span>
  <span class="sr-only">(required)</span>
{/snippet}

{#snippet detailFields()}
  <div class="grid grid-cols-2 gap-3 @md:grid-cols-3 @3xl:grid-cols-5">
    <div class="flex flex-col gap-1">
      <Label for="{uid}-fat" size="sm" variant="muted">
        Fat <span class="text-2xs font-normal text-foreground/30">
          optional
        </span>
      </Label>
      <div class="flex items-center gap-2">
        <Input
          type="number"
          id="{uid}-fat"
          name="fat"
          bind:value={draft.fat}
          min="0"
          step="0.1"
        />
        <span class="shrink-0 text-xs text-muted-foreground">g</span>
      </div>
    </div>

    <div class="flex flex-col gap-1">
      <Label for="{uid}-protein" size="sm" variant="muted">
        Protein <span class="text-2xs font-normal text-foreground/30">
          optional
        </span>
      </Label>
      <div class="flex items-center gap-2">
        <Input
          type="number"
          id="{uid}-protein"
          name="protein"
          bind:value={draft.protein}
          min="0"
          step="0.1"
        />
        <span class="shrink-0 text-xs text-muted-foreground">g</span>
      </div>
    </div>

    <div class="flex flex-col gap-1">
      <Label for="{uid}-energy" size="sm" variant="muted">
        Energy <span class="text-2xs font-normal text-foreground/30">
          optional
        </span>
      </Label>
      <div class="flex items-center gap-2">
        <Input
          type="number"
          id="{uid}-energy"
          name="energy"
          bind:value={() => energyInput, setEnergy}
          onblur={() => (energyInput = displayedEnergy())}
          min="0"
          step="1"
        />
        <span class="shrink-0 text-xs text-muted-foreground">{energyUnit}</span>
      </div>
    </div>

    <div class="col-span-2 flex flex-col gap-1">
      <Label for="{uid}-category" size="sm" variant="muted">Category</Label>
      <CategorySubcategoryCombobox
        id="{uid}-category"
        category={draft.category ?? ""}
        subcategory={draft.subcategory ?? ""}
        {categories}
        onCategoryChange={(category) => (draft.category = category)}
        onSubcategoryChange={(subcategory) => (draft.subcategory = subcategory)}
        {onCategoryCreate}
        {onSubcategoryCreate}
      />
    </div>
  </div>
{/snippet}

<div class="@container">
  <div
    class="grid grid-cols-2 items-start gap-3 @md:grid-cols-[1.6fr_110px_110px] @6xl:grid-cols-[1.6fr_110px_110px_1fr_1.4fr]"
  >
    <div class="col-span-2 flex flex-col gap-1 @md:col-span-1">
      <Label for="{uid}-name" size="sm" variant="muted">
        Name {@render required()}
      </Label>
      <Input
        id="{uid}-name"
        name="name"
        type="text"
        required
        placeholder="e.g. Greek yogurt, plain"
        bind:ref={nameRef}
        bind:value={draft.name}
      />
    </div>

    <div class="flex flex-col gap-1">
      <Label for="{uid}-carbs" size="sm">
        <span class="text-entry-carbs">Carbs</span>
        {@render required()}
      </Label>
      <InputGroup.Root>
        <InputGroup.Input
          id="{uid}-carbs"
          name="carbs"
          type="number"
          required
          bind:value={draft.carbs}
          min="0"
          step="0.1"
        />
        <InputGroup.Addon align="inline-end">g</InputGroup.Addon>
      </InputGroup.Root>
      <span class="text-muted-foreground text-2xs">
        per {draft.portion ?? 100}
        {draft.unit ?? "g"}
      </span>
    </div>

    <div class="flex flex-col gap-1">
      <Label for="{uid}-portion" size="sm" variant="muted">
        Portion {@render required()}
      </Label>
      <InputGroup.Root>
        <InputGroup.Input
          id="{uid}-portion"
          name="portion"
          type="number"
          required
          bind:value={draft.portion}
          min="0"
          step="1"
        />
        <InputGroup.Addon align="inline-end">
          {draft.unit ?? "g"}
        </InputGroup.Addon>
      </InputGroup.Root>
    </div>

    <div class="col-span-2 flex flex-col gap-1 @md:col-span-3 @6xl:col-span-1">
      <Label id="{uid}-unit-label" size="sm" variant="muted">Unit</Label>
      <ToggleGroup.Root
        aria-labelledby="{uid}-unit-label"
        type="single"
        value={draft.unit ?? "g"}
        onValueChange={(v: string) => {
          if (v) draft.unit = v;
        }}
        variant="outline"
        size="sm"
        class="w-full"
      >
        {#each FOOD_UNITS as u (u)}
          <ToggleGroup.Item value={u} class="flex-1">{u}</ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
    </div>

    <div class="col-span-2 flex flex-col gap-1 @md:col-span-3 @6xl:col-span-1">
      <Label id="{uid}-gi-label" size="sm" variant="muted">
        Glycemic Index
      </Label>
      <ToggleGroup.Root
        aria-labelledby="{uid}-gi-label"
        type="single"
        value={giFromInt(draft.gi)}
        onValueChange={(v: string) => {
          if (isGiLevel(v)) draft.gi = giToInt(v);
        }}
        variant="outline"
        size="sm"
        class="w-full"
      >
        {#each giLevels as g (g)}
          <ToggleGroup.Item value={g}>
            <GiIcon level={g} size={7} /><GiLabel level={g} />
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
    </div>
  </div>

  {#if collapseDetails}
    <Collapsible.Root bind:open={showDetails}>
      <Collapsible.Content>
        <div class="mt-3">{@render detailFields()}</div>
      </Collapsible.Content>
      <div
        class="mt-3 flex flex-col gap-3 @md:flex-row @md:items-center @md:justify-between @md:gap-0"
      >
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button
              {...props}
              data-testid="food-composer-details"
              variant="subtle"
              size="inline-xs"
              class="select-none"
            >
              <span
                class="inline-flex transition-transform"
                class:rotate-90={showDetails}
              >
                <ChevronRight class="h-3 w-3" />
              </span>
              {showDetails ? "Hide" : "Add"} fat, protein, category...
            </Button>
          {/snippet}
        </Collapsible.Trigger>
        {@render actions?.()}
      </div>
    </Collapsible.Root>
  {:else}
    <div class="mt-3">{@render detailFields()}</div>
    {#if actions}
      <div class="mt-3 flex flex-wrap items-center justify-end gap-2">
        {@render actions()}
      </div>
    {/if}
  {/if}
</div>
