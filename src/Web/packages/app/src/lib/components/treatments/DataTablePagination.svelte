<script lang="ts" generics="TData">
  import { Button } from "$lib/components/ui/button";
  import * as Select from "$lib/components/ui/select";
  import {
    ChevronLeft,
    ChevronRight,
    ChevronsLeft,
    ChevronsRight,
  } from "lucide-svelte";
  import type { Table } from "@tanstack/table-core";

  interface Props {
    table: Table<TData>;
    selectedCount: number;
    totalCount: number;
  }

  let { table, selectedCount, totalCount }: Props = $props();

  let pageSize = $derived(table.getState().pagination.pageSize);
</script>

<!-- Pagination -->
<div class="@container">
<div class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between px-2">
  <div class="text-sm text-muted-foreground">
    {selectedCount} of {totalCount} row(s) selected
  </div>
  <div class="flex items-center gap-6 @3xl:gap-8">
    <div class="flex items-center gap-2">
      <p class="text-sm font-medium">Rows per page</p>
      <Select.Root
        type="single"
        value={String(pageSize)}
        onValueChange={(v) => {
          pageSize = Number(v);
          table.setPageSize(pageSize);
        }}
      >
        <Select.Trigger size="sm" class="w-20" aria-label="Rows per page">
          {pageSize}
        </Select.Trigger>
        <Select.Content>
          {#each [25, 50, 100, 200] as size (size)}
            <Select.Item value={String(size)} label={String(size)} />
          {/each}
        </Select.Content>
      </Select.Root>
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
</div>
