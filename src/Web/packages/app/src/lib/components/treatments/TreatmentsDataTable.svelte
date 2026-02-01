<script lang="ts" module>
  import type { Treatment } from "$lib/api";
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
  import { getEventTypeStyle } from "$lib/constants/treatment-categories";
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
  import * as Tooltip from "$lib/components/ui/tooltip";
  import {
    ArrowUpDown,
    ArrowUp,
    ArrowDown,
    ChevronLeft,
    ChevronRight,
    ChevronsLeft,
    ChevronsRight,
    Edit,
    Trash2,
    Columns3,
    Filter,
    X,
    Braces,
    Check,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { formatDateTimeCompact } from "$lib/utils/formatting";

  interface Props {
    treatments: Treatment[];
    onEdit?: (treatment: Treatment) => void;
    onDelete?: (treatment: Treatment) => void;
    onBulkDelete?: (treatments: Treatment[]) => void;
  }

  let { treatments, onEdit, onDelete, onBulkDelete }: Props = $props();

  // Table state
  let sorting = $state<SortingState>([{ id: "time", desc: true }]);
  let columnFilters = $state<ColumnFiltersState>([]);
  let columnVisibility = $state<VisibilityState>({});
  let rowSelection = $state<Record<string, boolean>>({});
  let pagination = $state<PaginationState>({ pageIndex: 0, pageSize: 50 });
  let globalFilter = $state("");

  // Column filter states
  let eventTypeFilterOpen = $state(false);
  let eventTypeFilterSearch = $state("");
  let selectedEventTypes = $state<string[]>([]);

  let sourceFilterOpen = $state(false);
  let sourceFilterSearch = $state("");
  let selectedSources = $state<string[]>([]);

  // Compute unique event types and sources from data
  let uniqueEventTypes = $derived.by(() => {
    const types = new Set<string>();
    for (const t of treatments) {
      if (t.eventType) types.add(t.eventType);
    }
    return Array.from(types).sort();
  });

  let uniqueSources = $derived.by(() => {
    const sources = new Set<string>();
    for (const t of treatments) {
      const source = t.data_source || t.enteredBy;
      if (source) sources.add(source);
    }
    return Array.from(sources).sort();
  });

  // Filtered lists for dropdowns
  let filteredEventTypesForDropdown = $derived.by(() => {
    if (!eventTypeFilterSearch.trim()) return uniqueEventTypes;
    const search = eventTypeFilterSearch.toLowerCase();
    return uniqueEventTypes.filter((type) =>
      type.toLowerCase().includes(search)
    );
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

  // Column definitions
  const columns: ColumnDef<Treatment, unknown>[] = [
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
      accessorFn: (row) => row.created_at,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Time" }),
      cell: ({ row }) => formatDateTimeCompact(row.original.created_at),
      sortingFn: (rowA, rowB) => {
        const a = new Date(rowA.original.created_at || 0).getTime();
        const b = new Date(rowB.original.created_at || 0).getTime();
        return a - b;
      },
    },
    // Event Type column
    {
      id: "eventType",
      accessorKey: "eventType",
      header: () => renderSnippet(eventTypeFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const eventType = row.original.eventType || "<none>";
        const styles = getEventTypeStyle(eventType);
        return renderSnippet(eventTypeBadgeSnippet as any, { eventType, styles });
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(row.original.eventType || "");
      },
    },
    // Insulin column
    {
      id: "insulin",
      accessorKey: "insulin",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Insulin" }),
      cell: ({ row }) => formatNumber(row.original.insulin, "U"),
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.insulin ?? 0;
        const b = rowB.original.insulin ?? 0;
        return a - b;
      },
    },
    // Rate column (for basal treatments)
    {
      id: "rate",
      accessorKey: "rate",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Rate" }),
      cell: ({ row }) => {
        const rate = row.original.rate ?? row.original.absolute;
        if (rate === undefined || rate === null) return "—";
        return `${rate.toFixed(2)} U/hr`;
      },
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.rate ?? rowA.original.absolute ?? 0;
        const b = rowB.original.rate ?? rowB.original.absolute ?? 0;
        return a - b;
      },
    },
    // Carbs column
    {
      id: "carbs",
      accessorKey: "carbs",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Carbs" }),
      cell: ({ row }) => formatNumber(row.original.carbs, "g"),
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.carbs ?? 0;
        const b = rowB.original.carbs ?? 0;
        return a - b;
      },
    },
    // Blood Glucose column
    {
      id: "glucose",
      accessorKey: "glucose",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "BG" }),
      cell: ({ row }) => formatNumber(row.original.glucose),
    },
    // Duration column
    {
      id: "duration",
      accessorKey: "duration",
      header: "Duration",
      cell: ({ row }) => {
        const duration = row.original.duration;
        if (!duration) return "—";
        return `${duration.toFixed(0)} min`;
      },
    },
    // Profile column
    {
      id: "profile",
      accessorKey: "profile",
      header: "Profile",
      cell: ({ row }) => row.original.profile || "—",
    },
    // Entered By column
    {
      id: "enteredBy",
      accessorKey: "enteredBy",
      header: () => renderSnippet(sourceFilterHeaderSnippet as any, {}),
      cell: ({ row }) => {
        const enteredBy = row.original.enteredBy;
        const source = row.original.data_source;
        // Show source first, then enteredBy as secondary
        const primary = source || enteredBy;
        if (!primary) return "—";
        // Build display with both if available and different
        let display =
          primary.length > 20 ? primary.slice(0, 20) + "…" : primary;
        if (source && enteredBy && source !== enteredBy) {
          const secondary =
            enteredBy.length > 15 ? enteredBy.slice(0, 15) + "…" : enteredBy;
          return renderSnippet(sourceSnippet as any, { primary: display, secondary });
        }
        return display;
      },
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        const source = row.original.data_source || row.original.enteredBy || "";
        return filterValue.includes(source);
      },
    },
    // Additional Properties column
    {
      id: "additionalProperties",
      accessorFn: (row) => row.additional_properties,
      header: "",
      cell: ({ row }) => {
        const props = row.original.additional_properties;
        if (!props || Object.keys(props).length === 0) return null;
        return renderSnippet(additionalPropertiesSnippet as any, { props });
      },
      enableSorting: false,
      size: 40,
    },
    // Notes column
    {
      id: "notes",
      accessorKey: "notes",
      header: "Notes",
      cell: ({ row }) => {
        const notes = row.original.notes || row.original.reason;
        if (!notes) return "—";
        return notes.length > 30 ? notes.slice(0, 30) + "…" : notes;
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
      size: 80,
    },
  ];

  // Create the table
  const table = createSvelteTable({
    get data() {
      return treatments;
    },
    columns,
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
      const values = [
        row.original.eventType,
        row.original.notes,
        row.original.enteredBy,
        row.original.reason,
        row.original.profile,
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
  let selectedTreatments = $derived.by(() => {
    return table.getSelectedRowModel().rows.map((row) => row.original);
  });

  function handleBulkDelete() {
    if (onBulkDelete && selectedTreatments.length > 0) {
      onBulkDelete(selectedTreatments);
    }
  }

  function clearSelection() {
    rowSelection = {};
  }

  // Filter helper functions
  function toggleEventTypeFilter(eventType: string) {
    if (selectedEventTypes.includes(eventType)) {
      selectedEventTypes = selectedEventTypes.filter((t) => t !== eventType);
    } else {
      selectedEventTypes = [...selectedEventTypes, eventType];
    }
    applyEventTypeFilter();
  }

  function clearEventTypeFilter() {
    selectedEventTypes = [];
    applyEventTypeFilter();
  }

  function applyEventTypeFilter() {
    const column = table.getColumn("eventType");
    if (column) {
      column.setFilterValue(
        selectedEventTypes.length > 0 ? selectedEventTypes : undefined
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
    const column = table.getColumn("enteredBy");
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

{#snippet eventTypeBadgeSnippet({
  eventType,
  styles,
}: {
  eventType: string;
  styles: any;
})}
  <Badge
    variant="outline"
    class="whitespace-nowrap {styles.colorClass} {styles.bgClass} {styles.borderClass}"
  >
    {eventType}
  </Badge>
{/snippet}

{#snippet sourceSnippet({
  primary,
  secondary,
}: {
  primary: string;
  secondary: string;
})}
  <div class="flex flex-col">
    <span class="text-sm">{primary}</span>
    <span class="text-xs text-muted-foreground">{secondary}</span>
  </div>
{/snippet}

{#snippet eventTypeFilterHeaderSnippet({})}
  <Popover.Root bind:open={eventTypeFilterOpen}>
    <Popover.Trigger>
      {#snippet child({ props })}
        <Button
          variant="ghost"
          size="sm"
          class="-ml-3 h-8 data-[state=open]:bg-accent gap-1"
          {...props}
        >
          Type
          {#if selectedEventTypes.length > 0}
            <Badge variant="secondary" class="ml-1 h-5 px-1 text-xs">
              {selectedEventTypes.length}
            </Badge>
          {/if}
          <Filter class="ml-1 h-3 w-3 opacity-50" />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-[220px] p-0" align="start">
      <Command.Root shouldFilter={false}>
        <Command.Input
          placeholder="Search types..."
          bind:value={eventTypeFilterSearch}
        />
        <Command.List class="max-h-[200px]">
          <Command.Empty>No types found.</Command.Empty>
          <Command.Group>
            {#each filteredEventTypesForDropdown as eventType}
              {@const typeStyle = getEventTypeStyle(eventType)}
              <Command.Item
                value={eventType}
                onSelect={() => toggleEventTypeFilter(eventType)}
                class="cursor-pointer"
              >
                <div
                  class={cn(
                    "mr-2 h-4 w-4 border rounded flex items-center justify-center",
                    selectedEventTypes.includes(eventType)
                      ? "bg-primary border-primary"
                      : "border-muted"
                  )}
                >
                  {#if selectedEventTypes.includes(eventType)}
                    <Check class="h-3 w-3 text-primary-foreground" />
                  {/if}
                </div>
                <span class={typeStyle.colorClass}>{eventType}</span>
              </Command.Item>
            {/each}
          </Command.Group>
        </Command.List>
        {#if selectedEventTypes.length > 0}
          <div class="border-t p-2">
            <Button
              variant="ghost"
              size="sm"
              class="w-full"
              onclick={clearEventTypeFilter}
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

{#snippet additionalPropertiesSnippet({ props }: { props: any })}
  <Tooltip.Root>
    <Tooltip.Trigger>
      {#snippet child({ props: triggerProps })}
        <div
          class="flex items-center justify-center h-6 w-6 rounded bg-muted/50 text-muted-foreground hover:bg-muted cursor-help"
          {...triggerProps}
        >
          <Braces class="h-3.5 w-3.5" />
        </div>
      {/snippet}
    </Tooltip.Trigger>
    <Tooltip.Content side="left" class="max-w-[300px]">
      <pre class="text-xs font-mono whitespace-pre-wrap">{JSON.stringify(
          props,
          null,
          2
        )}</pre>
    </Tooltip.Content>
  </Tooltip.Root>
{/snippet}

{#snippet actionsSnippet({ treatment }: { treatment: Treatment })}
  <div class="flex items-center gap-1">
    {#if onEdit}
      <Button
        variant="ghost"
        size="sm"
        class="h-8 w-8 p-0"
        onclick={() => onEdit(treatment)}
        title="Edit treatment"
      >
        <Edit class="h-4 w-4" />
      </Button>
    {/if}
    {#if onDelete}
      <Button
        variant="ghost"
        size="sm"
        class="h-8 w-8 p-0 text-destructive hover:text-destructive"
        onclick={() => onDelete(treatment)}
        title="Delete treatment"
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
        placeholder="Search treatments..."
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
  {#if selectedTreatments.length > 0}
    <div
      class="flex items-center justify-between rounded-md border bg-muted/50 px-4 py-2"
    >
      <span class="text-sm font-medium">
        {selectedTreatments.length} treatment{selectedTreatments.length !== 1
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
              No treatments found.
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
