<script lang="ts">
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Badge } from "$lib/components/ui/badge";
  import { ChevronDown, ChevronRight } from "lucide-svelte";
  import SupplyItem from "./supply-item.svelte";
  import type { SupplyCategoryConfig } from "./packing-config";
  import type { Component } from "svelte";

  interface ItemState {
    enabled: boolean;
    quantity: number;
  }

  interface Props {
    config: SupplyCategoryConfig;
    icon: Component<{ class?: string }>;
    tripDays: number;
    avgTdd: number | null;
    eventIntervals: Record<string, number>;
    itemStates: ItemState[];
  }

  const { config, icon: Icon, tripDays, avgTdd, eventIntervals, itemStates = $bindable([]) }: Props =
    $props();

  let expanded = $state(true);

  const categoryTotal = $derived(
    itemStates.reduce((sum, s) => sum + (s.enabled ? s.quantity : 0), 0)
  );

  function getHintInterval(item: SupplyCategoryConfig["items"][number]): number | null {
    if (!item.hintEventTypes) return null;
    for (const eventType of item.hintEventTypes) {
      if (eventIntervals[eventType] != null) {
        return eventIntervals[eventType];
      }
    }
    return null;
  }
</script>

<Collapsible.Root bind:open={expanded}>
  <Card>
    <Collapsible.Trigger class="w-full">
      <CardHeader interactive class="py-3">
        <div class="flex items-center justify-between">
          <CardTitle class="flex items-center gap-2 text-sm font-semibold">
            <Icon class="h-4 w-4 text-muted-foreground" />
            {config.label}
            {#if categoryTotal > 0}
              <Badge variant="secondary" class="tabular-nums">
                {categoryTotal} items
              </Badge>
            {/if}
          </CardTitle>
          {#if expanded}
            <ChevronDown class="h-4 w-4 text-muted-foreground" />
          {:else}
            <ChevronRight class="h-4 w-4 text-muted-foreground" />
          {/if}
        </div>
      </CardHeader>
    </Collapsible.Trigger>
    <Collapsible.Content>
      <CardContent class="pt-0 pb-3">
        {#each config.items as item, i (item.id)}
          {#if itemStates[i]}
            <SupplyItem
              config={item}
              {tripDays}
              {avgTdd}
              hintInterval={getHintInterval(item)}
              bind:enabled={itemStates[i].enabled}
              bind:quantity={itemStates[i].quantity}
            />
          {/if}
        {/each}
      </CardContent>
    </Collapsible.Content>
  </Card>
</Collapsible.Root>
