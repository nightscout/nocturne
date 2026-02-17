<script lang="ts" module>
  import type {
    TreatmentRow,
  } from "$lib/constants/treatment-categories";
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
  import { getRowTypeStyle } from "$lib/constants/treatment-categories";
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

  interface Props {
    rows: TreatmentRow[];
    onDelete?: (row: TreatmentRow) => void;
    onBulkDelete?: (rows: TreatmentRow[]) => void;
  }

  let { rows, onDelete, onBulkDelete }: Props = $props();

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

  // Compute unique sources from data
  let uniqueSources = $derived.by(() => {
    const sources = new Set<string>();
    for (const r of rows) {
      const source = r.dataSource || r.app;
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
    if (value === undefined || value === null) return "—";
    const formatted = Number.isInteger(value)
      ? value.toString()
      : value.toFixed(1);
    return unit ? `${formatted}${unit}` : formatted;
  }

  function formatMills(mills: number | undefined): string {
    if (!mills) return "—";
    return formatDateTimeCompact(new Date(mills).toISOString());
  }

  // Column definitions
  const columns: ColumnDef<TreatmentRow, unknown>[] = [
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
      accessorFn: (row) => row.mills,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Time" }),
      cell: ({ row }) => formatMills(row.original.mills),
      sortingFn: (rowA, rowB) =>
        (rowA.original.mills ?? 0) - (rowB.original.mills ?? 0),
    },
    // Type column (bolus / carb intake)
    {
      id: "type",
      accessorFn: (row) => row.kind,
      header: () => renderSnippet(typeFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const label = row.original.kind === "bolus" ? "Bolus" : "Carb Intake";
        const styles = getRowTypeStyle(row.original.kind);
        return renderSnippet(typeBadgeSnippet as any, { label, styles });
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(row.original.kind);
      },
    },
    // Insulin column (bolus only)
    {
      id: "insulin",
      accessorFn: (row) => (row.kind === "bolus" ? row.insulin : undefined),
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Insulin",
        }),
      cell: ({ row }) => {
        if (row.original.kind !== "bolus") return "—";
        return formatNumber(row.original.insulin, "U");
      },
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.kind === "bolus" ? (rowA.original.insulin ?? 0) : 0;
        const b = rowB.original.kind === "bolus" ? (rowB.original.insulin ?? 0) : 0;
        return a - b;
      },
    },
    // Carbs column (carb intake only)
    {
      id: "carbs",
      accessorFn: (row) => (row.kind === "carbIntake" ? row.carbs : undefined),
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Carbs" }),
      cell: ({ row }) => {
        if (row.original.kind !== "carbIntake") return "—";
        return formatNumber(row.original.carbs, "g");
      },
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.kind === "carbIntake" ? (rowA.original.carbs ?? 0) : 0;
        const b = rowB.original.kind === "carbIntake" ? (rowB.original.carbs ?? 0) : 0;
        return a - b;
      },
    },
    // Details column (context-specific: bolusType/automatic for bolus, foodType for carbs)
    {
      id: "details",
      header: "Details",
      cell: ({ row }) => {
        if (row.original.kind === "bolus") {
          return renderSnippet(bolusDetailsSnippet as any, {
            bolusType: row.original.bolusType,
            automatic: row.original.automatic,
          });
        }
        const foodType = row.original.foodType;
        if (!foodType) return "—";
        return foodType.length > 25 ? foodType.slice(0, 25) + "…" : foodType;
      },
      enableSorting: false,
    },
    // Duration column
    {
      id: "duration",
      header: "Duration",
      accessorFn: (row) => {
        if (row.kind === "bolus") return row.duration;
        return row.absorptionTime;
      },
      cell: ({ row }) => {
        const val =
          row.original.kind === "bolus"
            ? row.original.duration
            : row.original.absorptionTime;
        if (!val) return "—";
        return `${val.toFixed(0)} min`;
      },
    },
    // Source column
    {
      id: "source",
      accessorFn: (row) => row.dataSource || row.app,
      header: () => renderSnippet(sourceFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const source = row.original.dataSource || row.original.app;
        if (!source) return "—";
        return source.length > 25 ? source.slice(0, 25) + "…" : source;
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        const source = row.original.dataSource || row.original.app || "";
        return filterValue.includes(source);
      },
    },
    // Actions column
    {
      id: "actions",
      header: "",
      cell: ({ row }) =>
        renderSnippet(actionsSnippet as any, { treatment: row.original }),
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
    getRowId: (row) => row.id ?? "",
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
      const values = [
        r.kind === "bolus" ? "bolus" : "carb intake",
        r.kind === "bolus" ? r.bolusType : r.foodType,
        r.dataSource,
        r.app,
        r.device,
      ]
        .filter(Boolean)
        .join(" ")
        .toLowerCase();
      return values.includes(search);
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

{#snippet bolusDetailsSnippet({
  bolusType,
  automatic,
}: {
  bolusType: string | undefined;
  automatic: boolean | undefined;
})}
  <div class="flex items-center gap-1.5">
    {#if bolusType}
      <span class="text-sm">{bolusType}</span>
    {/if}
    {#if automatic}
      <Badge variant="secondary" class="text-[10px] px-1 py-0">Auto</Badge>
    {/if}
    {#if !bolusType && !automatic}
      <span>—</span>
    {/if}
  </div>
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
    <Popover.Content class="w-[180px] p-0" align="start">
      <Command.Root shouldFilter={false}>
        <Command.List>
          <Command.Group>
            {#each [{ value: "bolus", label: "Bolus" }, { value: "carbIntake", label: "Carb Intake" }] as option}
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

{#snippet actionsSnippet({ treatment }: { treatment: TreatmentRow })}
  <div class="flex items-center gap-1">
    {#if onDelete}
      <Button
        variant="ghost"
        size="sm"
        class="h-8 w-8 p-0 text-destructive hover:text-destructive"
        onclick={() => onDelete(treatment)}
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
          <Table.Row data-state={row.getIsSelected() ? "selected" : undefined}>
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
