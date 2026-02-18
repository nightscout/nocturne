<script lang="ts" module>
  import type {
    EntryRecord,
    EntryCategoryId,
  } from "$lib/constants/entry-categories";
  import { getEntryStyle } from "$lib/constants/entry-categories";
  import type {
    ColumnDef,
    SortingState,
    ColumnFiltersState,
    VisibilityState,
    PaginationState,
  } from "@tanstack/table-core";
  import {
    getCoreRowModel,
    getSortedRowModel,
    getFilteredRowModel,
    getPaginationRowModel,
  } from "@tanstack/table-core";
  import {
    createSvelteTable,
    FlexRender,
    renderSnippet,
  } from "$lib/components/ui/data-table";
</script>

<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import * as Popover from "$lib/components/ui/popover";
  import * as Command from "$lib/components/ui/command";
  import * as Table from "$lib/components/ui/table";
  import {
    ArrowUpDown,
    ArrowUp,
    ArrowDown,
    ChevronLeft,
    ChevronRight,
    ChevronsLeft,
    ChevronsRight,
    Trash2,
    Columns3,
    Filter,
    X,
    Check,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { formatDateTimeCompact } from "$lib/utils/formatting";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";

  interface Props {
    rows: EntryRecord[];
    onDelete?: (row: EntryRecord) => void;
    onBulkDelete?: (rows: EntryRecord[]) => void;
    onRowClick?: (row: EntryRecord) => void;
  }

  let { rows, onDelete, onBulkDelete, onRowClick }: Props = $props();

  // Table state
  let sorting = $state<SortingState>([{ id: "time", desc: true }]);
  let columnFilters = $state<ColumnFiltersState>([]);
  let columnVisibility = $state<VisibilityState>({});
  let rowSelection = $state<Record<string, boolean>>({});
  let pagination = $state<PaginationState>({ pageIndex: 0, pageSize: 50 });
  let globalFilter = $state("");

  // Column filter states
  let typeFilterOpen = $state(false);
  let selectedTypes = $state<string[]>([]);

  let sourceFilterOpen = $state(false);
  let sourceFilterSearch = $state("");
  let selectedSources = $state<string[]>([]);

  // Map category IDs to display labels
  const categoryLabels: Record<EntryCategoryId, string> = {
    bolus: "Bolus",
    carbs: "Carb Intake",
    bgCheck: "BG Check",
    note: "Note",
    deviceEvent: "Device Event",
  };

  // Compute unique sources from data
  let uniqueSources = $derived.by(() => {
    const sources = new Set<string>();
    for (const r of rows) {
      const source = r.data.dataSource || r.data.app;
      if (source) sources.add(source);
    }
    return Array.from(sources).sort();
  });

  let filteredSourcesForDropdown = $derived.by(() => {
    if (!sourceFilterSearch.trim()) return uniqueSources;
    const search = sourceFilterSearch.toLowerCase();
    return uniqueSources.filter((source) =>
      source.toLowerCase().includes(search)
    );
  });

  // Format functions
  function formatNumber(
    value: number | undefined | null,
    unit?: string
  ): string {
    if (value === undefined || value === null) return "\u2014";
    const formatted = Number.isInteger(value)
      ? value.toString()
      : value.toFixed(1);
    return unit ? `${formatted}${unit}` : formatted;
  }

  function formatMills(mills: number | undefined): string {
    if (!mills) return "\u2014";
    return formatDateTimeCompact(new Date(mills).toISOString());
  }

  /** Get the primary value column content for an entry record */
  function getPrimaryValue(record: EntryRecord): string {
    switch (record.kind) {
      case "bolus":
        return formatNumber(record.data.insulin, "U");
      case "carbs":
        return formatNumber(record.data.carbs, "g");
      case "bgCheck":
        return formatNumber(record.data.mgdl, " mg/dL");
      case "note":
        return record.data.text
          ? record.data.text.length > 40
            ? record.data.text.slice(0, 40) + "\u2026"
            : record.data.text
          : "\u2014";
      case "deviceEvent":
        return record.data.eventType ?? "\u2014";
    }
  }

  /** Get details column content */
  function getDetails(record: EntryRecord): string {
    switch (record.kind) {
      case "bolus": {
        const parts: string[] = [];
        if (record.data.bolusType) parts.push(record.data.bolusType);
        if (record.data.automatic) parts.push("Auto");
        return parts.length > 0 ? parts.join(" \u00B7 ") : "\u2014";
      }
      case "carbs": {
        const foodType = record.data.foodType;
        if (!foodType) return "\u2014";
        return foodType.length > 30 ? foodType.slice(0, 30) + "\u2026" : foodType;
      }
      case "bgCheck":
        return record.data.glucoseType ?? "\u2014";
      case "note": {
        const parts: string[] = [];
        if (record.data.eventType) parts.push(record.data.eventType);
        if (record.data.isAnnouncement) parts.push("Announcement");
        return parts.length > 0 ? parts.join(" \u00B7 ") : "\u2014";
      }
      case "deviceEvent":
        return record.data.notes
          ? record.data.notes.length > 30
            ? record.data.notes.slice(0, 30) + "\u2026"
            : record.data.notes
          : "\u2014";
    }
  }

  // Column definitions
  const columns: ColumnDef<EntryRecord, unknown>[] = [
    // Selection column
    {
      id: "select",
      header: ({ table }) => {
        const checked = table.getIsAllPageRowsSelected();
        const indeterminate = table.getIsSomePageRowsSelected();
        return renderSnippet(selectHeaderSnippet as any, {
          checked,
          indeterminate,
          table,
        });
      },
      cell: ({ row }) => {
        const checked = row.getIsSelected();
        return renderSnippet(selectCellSnippet as any, { checked, row });
      },
      enableSorting: false,
      enableHiding: false,
      size: 40,
    },
    // Time column
    {
      id: "time",
      accessorFn: (row) => row.data.mills,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Time" }),
      cell: ({ row }) => formatMills(row.original.data.mills),
      sortingFn: (rowA, rowB) =>
        (rowA.original.data.mills ?? 0) - (rowB.original.data.mills ?? 0),
    },
    // Type column
    {
      id: "type",
      accessorFn: (row) => row.kind,
      header: () => renderSnippet(typeFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const label = categoryLabels[row.original.kind];
        const styles = getEntryStyle(row.original.kind);
        return renderSnippet(typeBadgeSnippet as any, { label, styles });
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(row.original.kind);
      },
    },
    // Value column (insulin/carbs/glucose/text/event type depending on kind)
    {
      id: "value",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Value",
        }),
      cell: ({ row }) => getPrimaryValue(row.original),
      accessorFn: (row) => {
        switch (row.kind) {
          case "bolus":
            return row.data.insulin ?? 0;
          case "carbs":
            return row.data.carbs ?? 0;
          case "bgCheck":
            return row.data.mgdl ?? 0;
          default:
            return 0;
        }
      },
      sortingFn: (rowA, rowB) => {
        const valA = (() => {
          switch (rowA.original.kind) {
            case "bolus":
              return rowA.original.data.insulin ?? 0;
            case "carbs":
              return rowA.original.data.carbs ?? 0;
            case "bgCheck":
              return rowA.original.data.mgdl ?? 0;
            default:
              return 0;
          }
        })();
        const valB = (() => {
          switch (rowB.original.kind) {
            case "bolus":
              return rowB.original.data.insulin ?? 0;
            case "carbs":
              return rowB.original.data.carbs ?? 0;
            case "bgCheck":
              return rowB.original.data.mgdl ?? 0;
            default:
              return 0;
          }
        })();
        return valA - valB;
      },
    },
    // Details column
    {
      id: "details",
      header: "Details",
      cell: ({ row }) => getDetails(row.original),
      enableSorting: false,
    },
    // Source column
    {
      id: "source",
      accessorFn: (row) => row.data.dataSource || row.data.app,
      header: () => renderSnippet(sourceFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const source = row.original.data.dataSource || row.original.data.app;
        if (!source) return "\u2014";
        return source.length > 25 ? source.slice(0, 25) + "\u2026" : source;
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        const source = row.original.data.dataSource || row.original.data.app || "";
        return filterValue.includes(source);
      },
    },
    // Actions column
    {
      id: "actions",
      header: "",
      cell: ({ row }) =>
        renderSnippet(actionsSnippet as any, { entry: row.original }),
      enableSorting: false,
      enableHiding: false,
      size: 50,
    },
  ];

  // Create the table
  const table = createSvelteTable({
    get data() {
      return rows;
    },
    columns,
    getRowId: (row) => row.data.id ?? "",
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getPaginationRowModel: getPaginationRowModel(),
    onSortingChange: (updater) => {
      sorting = typeof updater === "function" ? updater(sorting) : updater;
    },
    onColumnFiltersChange: (updater) => {
      columnFilters =
        typeof updater === "function" ? updater(columnFilters) : updater;
    },
    onColumnVisibilityChange: (updater) => {
      columnVisibility =
        typeof updater === "function" ? updater(columnVisibility) : updater;
    },
    onRowSelectionChange: (updater) => {
      rowSelection =
        typeof updater === "function" ? updater(rowSelection) : updater;
    },
    onPaginationChange: (updater) => {
      pagination =
        typeof updater === "function" ? updater(pagination) : updater;
    },
    onGlobalFilterChange: (updater) => {
      globalFilter =
        typeof updater === "function" ? updater(globalFilter) : updater;
    },
    globalFilterFn: (row, _columnId, filterValue) => {
      const search = filterValue.toLowerCase();
      const r = row.original;
      const values: string[] = [categoryLabels[r.kind]];

      switch (r.kind) {
        case "bolus":
          if (r.data.bolusType) values.push(r.data.bolusType);
          break;
        case "carbs":
          if (r.data.foodType) values.push(r.data.foodType);
          break;
        case "bgCheck":
          if (r.data.glucoseType) values.push(r.data.glucoseType);
          break;
        case "note":
          if (r.data.text) values.push(r.data.text);
          if (r.data.eventType) values.push(r.data.eventType);
          break;
        case "deviceEvent":
          if (r.data.eventType) values.push(r.data.eventType);
          if (r.data.notes) values.push(r.data.notes);
          break;
      }

      if (r.data.dataSource) values.push(r.data.dataSource);
      if (r.data.app) values.push(r.data.app);
      if (r.data.device) values.push(r.data.device);

      return values.join(" ").toLowerCase().includes(search);
    },
    state: {
      get sorting() {
        return sorting;
      },
      get columnFilters() {
        return columnFilters;
      },
      get columnVisibility() {
        return columnVisibility;
      },
      get rowSelection() {
        return rowSelection;
      },
      get pagination() {
        return pagination;
      },
      get globalFilter() {
        return globalFilter;
      },
    },
    enableRowSelection: true,
  });

  // Selected rows
  let selectedRows = $derived.by(() => {
    return table.getSelectedRowModel().rows.map((row) => row.original);
  });

  function handleBulkDelete() {
    if (onBulkDelete && selectedRows.length > 0) {
      onBulkDelete(selectedRows);
    }
  }

  function clearSelection() {
    rowSelection = {};
  }

  // Filter helper functions
  function toggleTypeFilter(kind: string) {
    if (selectedTypes.includes(kind)) {
      selectedTypes = selectedTypes.filter((t) => t !== kind);
    } else {
      selectedTypes = [...selectedTypes, kind];
    }
    applyTypeFilter();
  }

  function clearTypeFilter() {
    selectedTypes = [];
    applyTypeFilter();
  }

  function applyTypeFilter() {
    const column = table.getColumn("type");
    if (column) {
      column.setFilterValue(
        selectedTypes.length > 0 ? selectedTypes : undefined
      );
    }
  }

  function toggleSourceFilter(source: string) {
    if (selectedSources.includes(source)) {
      selectedSources = selectedSources.filter((s) => s !== source);
    } else {
      selectedSources = [...selectedSources, source];
    }
    applySourceFilter();
  }

  function clearSourceFilter() {
    selectedSources = [];
    applySourceFilter();
  }

  function applySourceFilter() {
    const column = table.getColumn("source");
    if (column) {
      column.setFilterValue(
        selectedSources.length > 0 ? selectedSources : undefined
      );
    }
  }

  const typeFilterOptions = Object.entries(ENTRY_CATEGORIES).map(([id, cat]) => ({
    value: id,
    label: cat.name,
  }));
</script>

<!-- Snippets for cell rendering -->
{#snippet selectHeaderSnippet({
  checked,
  indeterminate,
  table: t,
}: {
  checked: boolean;
  indeterminate: boolean;
  table: typeof table;
})}
  <Checkbox
    {checked}
    {indeterminate}
    onCheckedChange={(value) => t.toggleAllPageRowsSelected(!!value)}
    aria-label="Select all"
  />
{/snippet}

{#snippet selectCellSnippet({ checked, row }: { checked: boolean; row: any })}
  <Checkbox
    {checked}
    onCheckedChange={(value) => row.toggleSelected(!!value)}
    aria-label="Select row"
  />
{/snippet}

{#snippet sortableHeaderSnippet({
  column,
  label,
}: {
  column: any;
  label: string;
})}
  <Button
    variant="ghost"
    size="sm"
    class="-ml-3 h-8 data-[state=open]:bg-accent"
    onclick={() => column.toggleSorting()}
  >
    {label}
    {#if column.getIsSorted() === "asc"}
      <ArrowUp class="ml-2 h-4 w-4" />
    {:else if column.getIsSorted() === "desc"}
      <ArrowDown class="ml-2 h-4 w-4" />
    {:else}
      <ArrowUpDown class="ml-2 h-4 w-4" />
    {/if}
  </Button>
{/snippet}

{#snippet typeBadgeSnippet({
  label,
  styles,
}: {
  label: string;
  styles: any;
})}
  <Badge
    variant="outline"
    class="whitespace-nowrap {styles.colorClass} {styles.bgClass} {styles.borderClass}"
  >
    {label}
  </Badge>
{/snippet}

{#snippet typeFilterHeaderSnippet({})}
  <Popover.Root bind:open={typeFilterOpen}>
    <Popover.Trigger>
      {#snippet child({ props })}
        <Button
          variant="ghost"
          size="sm"
          class="-ml-3 h-8 data-[state=open]:bg-accent gap-1"
          {...props}
        >
          Type
          {#if selectedTypes.length > 0}
            <Badge variant="secondary" class="ml-1 h-5 px-1 text-xs">
              {selectedTypes.length}
            </Badge>
          {/if}
          <Filter class="ml-1 h-3 w-3 opacity-50" />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-[200px] p-0" align="start">
      <Command.Root shouldFilter={false}>
        <Command.List>
          <Command.Group>
            {#each typeFilterOptions as option}
              <Command.Item
                value={option.value}
                onSelect={() => toggleTypeFilter(option.value)}
                class="cursor-pointer"
              >
                <div
                  class={cn(
                    "mr-2 h-4 w-4 border rounded flex items-center justify-center",
                    selectedTypes.includes(option.value)
                      ? "bg-primary border-primary"
                      : "border-muted"
                  )}
                >
                  {#if selectedTypes.includes(option.value)}
                    <Check class="h-3 w-3 text-primary-foreground" />
                  {/if}
                </div>
                <span>{option.label}</span>
              </Command.Item>
            {/each}
          </Command.Group>
        </Command.List>
        {#if selectedTypes.length > 0}
          <div class="border-t p-2">
            <Button
              variant="ghost"
              size="sm"
              class="w-full"
              onclick={clearTypeFilter}
            >
              <X class="mr-2 h-3 w-3" />
              Clear filter
            </Button>
          </div>
        {/if}
      </Command.Root>
    </Popover.Content>
  </Popover.Root>
{/snippet}

{#snippet sourceFilterHeaderSnippet({})}
  <Popover.Root bind:open={sourceFilterOpen}>
    <Popover.Trigger>
      {#snippet child({ props })}
        <Button
          variant="ghost"
          size="sm"
          class="-ml-3 h-8 data-[state=open]:bg-accent gap-1"
          {...props}
        >
          Source
          {#if selectedSources.length > 0}
            <Badge variant="secondary" class="ml-1 h-5 px-1 text-xs">
              {selectedSources.length}
            </Badge>
          {/if}
          <Filter class="ml-1 h-3 w-3 opacity-50" />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-[220px] p-0" align="start">
      <Command.Root shouldFilter={false}>
        <Command.Input
          placeholder="Search sources..."
          bind:value={sourceFilterSearch}
        />
        <Command.List class="max-h-[200px]">
          <Command.Empty>No sources found.</Command.Empty>
          <Command.Group>
            {#each filteredSourcesForDropdown as source}
              <Command.Item
                value={source}
                onSelect={() => toggleSourceFilter(source)}
                class="cursor-pointer"
              >
                <div
                  class={cn(
                    "mr-2 h-4 w-4 border rounded flex items-center justify-center",
                    selectedSources.includes(source)
                      ? "bg-primary border-primary"
                      : "border-muted"
                  )}
                >
                  {#if selectedSources.includes(source)}
                    <Check class="h-3 w-3 text-primary-foreground" />
                  {/if}
                </div>
                <span class="truncate">{source}</span>
              </Command.Item>
            {/each}
          </Command.Group>
        </Command.List>
        {#if selectedSources.length > 0}
          <div class="border-t p-2">
            <Button
              variant="ghost"
              size="sm"
              class="w-full"
              onclick={clearSourceFilter}
            >
              <X class="mr-2 h-3 w-3" />
              Clear filter
            </Button>
          </div>
        {/if}
      </Command.Root>
    </Popover.Content>
  </Popover.Root>
{/snippet}

{#snippet actionsSnippet({ entry }: { entry: EntryRecord })}
  <div class="flex items-center gap-1">
    {#if onDelete}
      <Button
        variant="ghost"
        size="sm"
        class="h-8 w-8 p-0 text-destructive hover:text-destructive"
        onclick={() => onDelete(entry)}
        title="Delete"
      >
        <Trash2 class="h-4 w-4" />
      </Button>
    {/if}
  </div>
{/snippet}

<!-- Table UI -->
<div class="space-y-4">
  <!-- Toolbar -->
  <div
    class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between"
  >
    <!-- Search -->
    <div class="flex flex-1 items-center gap-2">
      <Input
        placeholder="Search records..."
        value={globalFilter}
        oninput={(e) => {
          globalFilter = e.currentTarget.value;
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
          {#snippet child({ props })}
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
              onCheckedChange={(value) => column.toggleVisibility(!!value)}
            >
              {column.id}
            </DropdownMenu.CheckboxItem>
          {/each}
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>
  </div>

  <!-- Selection actions bar -->
  {#if selectedRows.length > 0}
    <div
      class="flex items-center justify-between rounded-md border bg-muted/50 px-4 py-2"
    >
      <span class="text-sm font-medium">
        {selectedRows.length} record{selectedRows.length !== 1
          ? "s"
          : ""} selected
      </span>
      <div class="flex items-center gap-2">
        <Button variant="outline" size="sm" onclick={clearSelection}>
          Clear
        </Button>
        {#if onBulkDelete}
          <Button variant="destructive" size="sm" onclick={handleBulkDelete}>
            <Trash2 class="mr-2 h-4 w-4" />
            Delete Selected
          </Button>
        {/if}
      </div>
    </div>
  {/if}

  <!-- Table -->
  <div class="rounded-md border">
    <Table.Root>
      <Table.Header>
        {#each table.getHeaderGroups() as headerGroup}
          <Table.Row>
            {#each headerGroup.headers as header}
              <Table.Head
                class="whitespace-nowrap"
                style={header.getSize()
                  ? `width: ${header.getSize()}px`
                  : undefined}
              >
                {#if !header.isPlaceholder}
                  <FlexRender
                    content={header.column.columnDef.header}
                    context={header.getContext()}
                  />
                {/if}
              </Table.Head>
            {/each}
          </Table.Row>
        {/each}
      </Table.Header>
      <Table.Body>
        {#each table.getRowModel().rows as row (row.id)}
          <Table.Row
            data-state={row.getIsSelected() ? "selected" : undefined}
            class={onRowClick ? "cursor-pointer" : ""}
            onclick={(e: MouseEvent) => {
              const target = e.target as HTMLElement;
              if (target.closest('button, input[type="checkbox"], [role="checkbox"]')) return;
              onRowClick?.(row.original);
            }}
          >
            {#each row.getVisibleCells() as cell}
              <Table.Cell class="py-2">
                <FlexRender
                  content={cell.column.columnDef.cell}
                  context={cell.getContext()}
                />
              </Table.Cell>
            {/each}
          </Table.Row>
        {:else}
          <Table.Row>
            <Table.Cell
              colspan={columns.length}
              class="h-24 text-center text-muted-foreground"
            >
              No records found.
            </Table.Cell>
          </Table.Row>
        {/each}
      </Table.Body>
    </Table.Root>
  </div>

  <!-- Pagination -->
  <div class="flex items-center justify-between px-2">
    <div class="text-sm text-muted-foreground">
      {table.getFilteredSelectedRowModel().rows.length} of {table.getFilteredRowModel()
        .rows.length} row(s) selected
    </div>
    <div class="flex items-center gap-6 lg:gap-8">
      <div class="flex items-center gap-2">
        <p class="text-sm font-medium">Rows per page</p>
        <select
          class="h-8 w-16 rounded-md border border-input bg-background text-sm"
          value={pagination.pageSize}
          onchange={(e) => {
            pagination = {
              ...pagination,
              pageSize: Number(e.currentTarget.value),
            };
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
        Page {pagination.pageIndex + 1} of {table.getPageCount()}
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
