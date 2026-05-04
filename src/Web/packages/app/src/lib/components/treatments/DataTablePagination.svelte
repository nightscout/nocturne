<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    ChevronLeft,
    ChevronRight,
    ChevronsLeft,
    ChevronsRight,
  } from "lucide-svelte";
  import type { Table } from "@tanstack/table-core";

  interface Props {
    table: Table<any>;
    selectedCount: number;
    totalCount: number;
  }

  let { table, selectedCount, totalCount }: Props = $props();

  let pageSize = $state(50);

  $effect(() => {
    pageSize = table.getState().pagination.pageSize;
  });
</script>

<!-- Pagination -->
<div class="flex items-center justify-between px-2">
  <div class="text-sm text-muted-foreground">
    {selectedCount} of {totalCount} row(s) selected
  </div>
  <div class="flex items-center gap-6 lg:gap-8">
    <div class="flex items-center gap-2">
      <p class="text-sm font-medium">Rows per page</p>
      <select
        class="h-8 w-16 rounded-md border border-input bg-background text-sm"
        value={pageSize}
        onchange={(e) => {
          table.setPageSize(Number(e.currentTarget.value));
        }}
      >
        {#each [25, 50, 100, 200] as size}
          <option value={size}>{size}</option>
        {/each}
      </select>
    </div>
    <div
      class="flex w-[100px] items-center justify-center text-sm font-medium"
    >
      Page {table.getState().pagination.pageIndex + 1} of {table.getPageCount()}
    </div>
    <div class="flex items-center gap-2">
      <Button
        variant="outline"
        size="sm"
        onclick={() => table.setPageIndex(0)}
        disabled={!table.getCanPreviousPage()}
      >
        <ChevronsLeft class="h-4 w-4" />
      </Button>
      <Button
        variant="outline"
        size="sm"
        onclick={() => table.previousPage()}
        disabled={!table.getCanPreviousPage()}
      >
        <ChevronLeft class="h-4 w-4" />
      </Button>
      <Button
        variant="outline"
        size="sm"
        onclick={() => table.nextPage()}
        disabled={!table.getCanNextPage()}
      >
        <ChevronRight class="h-4 w-4" />
      </Button>
      <Button
        variant="outline"
        size="sm"
        onclick={() => table.setPageIndex(table.getPageCount() - 1)}
        disabled={!table.getCanNextPage()}
      >
        <ChevronsRight class="h-4 w-4" />
      </Button>
    </div>
  </div>
</div>
