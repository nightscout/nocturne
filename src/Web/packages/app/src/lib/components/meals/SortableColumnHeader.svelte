<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { ArrowUp, ArrowDown, ArrowUpDown } from "lucide-svelte";

  let {
    label,
    column,
    sortColumn,
    sortDirection,
    onSort,
  }: {
    label: string;
    column: string;
    sortColumn: string;
    sortDirection: "asc" | "desc";
    onSort: (column: string) => void;
  } = $props();
</script>

<Button
  variant="ghost"
  size="sm"
  class="-ml-3"
  onclick={() => onSort(column)}
>
  {label}
  {#if sortColumn === column}
    {#if sortDirection === "asc"}
      <ArrowUp class="ml-1 h-4 w-4" />
    {:else}
      <ArrowDown class="ml-1 h-4 w-4" />
    {/if}
  {:else}
    <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
  {/if}
</Button>
