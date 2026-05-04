<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import { Columns3, Trash2 } from "lucide-svelte";
  import type { Table } from "@tanstack/table-core";

  interface Props {
    globalFilter: string;
    table: Table<any>;
    selectedCount: number;
    onClearSelection: () => void;
    onBulkDelete?: () => void;
  }

  let { globalFilter = $bindable(), table, selectedCount, onClearSelection, onBulkDelete }: Props = $props();
</script>

<!-- Toolbar -->
<div class="space-y-4">
  <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
    <!-- Search -->
    <div class="flex flex-1 items-center gap-2">
      <Input
        placeholder="Search records..."
        value={globalFilter}
        oninput={(e: Event) => {
          globalFilter = (e.currentTarget as HTMLInputElement).value;
        }}
        class="max-w-sm"
      />
      {#if globalFilter}
        <Button variant="ghost" size="sm" onclick={() => (globalFilter = "")}>
          Clear
        </Button>
      {/if}
    </div>

    <!-- Right side controls -->
    <div class="flex items-center gap-2">
      <!-- Column visibility dropdown -->
      <DropdownMenu.Root>
        <DropdownMenu.Trigger>
          {#snippet child({ props }: any)}
            <Button variant="outline" size="sm" class="ml-auto" {...props}>
              <Columns3 class="mr-2 h-4 w-4" />
              Columns
            </Button>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content align="end">
          {#each table
            .getAllColumns()
            .filter((col) => col.getCanHide()) as column}
            <DropdownMenu.CheckboxItem
              checked={column.getIsVisible()}
              onCheckedChange={(value: any) => column.toggleVisibility(!!value)}
            >
              {column.id}
            </DropdownMenu.CheckboxItem>
          {/each}
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>
  </div>

  <!-- Selection actions bar -->
  {#if selectedCount > 0}
    <div
      class="flex items-center justify-between rounded-md border bg-muted/50 px-4 py-2"
    >
      <span class="text-sm font-medium">
        {selectedCount} record{selectedCount !== 1 ? "s" : ""} selected
      </span>
      <div class="flex items-center gap-2">
        <Button variant="outline" size="sm" onclick={onClearSelection}>
          Clear
        </Button>
        {#if onBulkDelete}
          <Button variant="destructive" size="sm" onclick={onBulkDelete}>
            <Trash2 class="mr-2 h-4 w-4" />
            Delete Selected
          </Button>
        {/if}
      </div>
    </div>
  {/if}
</div>
