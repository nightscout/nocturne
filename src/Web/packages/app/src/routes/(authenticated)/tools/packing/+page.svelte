<script lang="ts">
  import { goto } from "$app/navigation";
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import {
    Luggage,
    Syringe,
    Activity,
    Cpu,
    TestTube,
    ShieldAlert,
    Info,
    ListChecks,
  } from "lucide-svelte";
  import SupplyCategory from "$lib/components/tools/packing/supply-category.svelte";
  import { categories } from "$lib/components/tools/packing/packing-config";
  import { getPackingHints } from "./packing.remote";

  const hintsQuery = getPackingHints();
  const hints = $derived(hintsQuery.current);
  let tripDays = $state(7);

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const iconMap: Record<string, any> = {
    Syringe,
    Activity,
    Cpu,
    TestTube,
    ShieldAlert,
  };

  // Track item states per category
  let categoryStates: Array<Array<{ enabled: boolean; quantity: number }>> = $state(
    categories.map((cat) =>
      cat.items.map((item) => ({
        enabled: item.defaultEnabled,
        quantity: 0,
      }))
    )
  );

  function generateList() {
    const items: Array<{ c: string; l: string; q: number }> = [];

    categories.forEach((cat, ci) => {
      cat.items.forEach((item, ii) => {
        const state = categoryStates[ci]?.[ii];
        if (state?.enabled && state.quantity > 0) {
          items.push({ c: cat.label, l: item.label, q: state.quantity });
        }
      });
    });

    const encoded = btoa(JSON.stringify(items));
    goto(`/tools/packing/list?d=${encodeURIComponent(encoded)}`);
  }

  const totalItems = $derived(
    categoryStates.reduce(
      (sum, cat) => sum + cat.reduce((s, item) => s + (item.enabled ? item.quantity : 0), 0),
      0
    )
  );
</script>

<div class="container mx-auto p-6 max-w-3xl space-y-5">
  <!-- Header with inline trip duration -->
  <div class="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
    <div>
      <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
        <Luggage class="h-6 w-6" />
        Packing Calculator
      </h1>
      <p class="text-muted-foreground text-sm">
        Configure what you need, then generate a shareable packing list.
      </p>
    </div>
    <div class="flex items-center gap-2">
      <Label class="text-sm font-medium whitespace-nowrap">Trip</Label>
      <Input
        type="number"
        bind:value={tripDays}
        min={1}
        max={365}
        step={1}
        class="w-20 h-9"
      />
      <span class="text-sm text-muted-foreground">days</span>
    </div>
  </div>

  <!-- TDD Hint -->
  {#if hints?.avgTdd}
    <div class="flex items-start gap-2 rounded-lg border border-border bg-muted/50 p-3 text-sm">
      <Info class="h-4 w-4 mt-0.5 text-muted-foreground shrink-0" />
      <p class="text-muted-foreground">
        Your 14-day average insulin use is
        <span class="font-semibold text-foreground">~{hints.avgTdd}u/day</span>.
      </p>
    </div>
  {/if}

  <!-- Category Cards -->
  {#each categories as category, ci}
    <SupplyCategory
      config={category}
      icon={iconMap[category.icon]}
      {tripDays}
      avgTdd={hints?.avgTdd ?? null}
      eventIntervals={hints?.eventIntervals ?? {}}
      bind:itemStates={categoryStates[ci]}
    />
  {/each}

  <!-- Generate Button -->
  <Card class="border-primary/20 bg-primary/5">
    <CardContent class="pt-6 flex flex-col items-center gap-3 text-center">
      <p class="text-sm text-muted-foreground">
        {#if totalItems > 0}
          {totalItems} items across your selected supplies
        {:else}
          Enable some supplies above to generate your list
        {/if}
      </p>
      <Button size="lg" disabled={totalItems === 0} onclick={generateList} class="gap-2">
        <ListChecks class="h-4 w-4" />
        Generate Packing List
      </Button>
    </CardContent>
  </Card>
</div>
