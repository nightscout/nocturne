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
  import * as Table from "$lib/components/ui/table";
  import {
    ArrowUpDown,
    ArrowUp,
    ArrowDown,
    Trash2,
  } from "lucide-svelte";
  import { formatDateTimeCompact, bg, bgLabel } from "$lib/utils/formatting";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import DataTableToolbar from "./DataTableToolbar.svelte";
  import DataTablePagination from "./DataTablePagination.svelte";
  import ColumnFilterPopover from "./ColumnFilterPopover.svelte";

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
  let selectedTypes = $state<string[]>([]);
  let selectedSources = $state<string[]>([]);

  // Map category IDs to display labels
  const categoryLabels: Record<EntryCategoryId, string> = {
    bolus: "Bolus",
    carbs: "Carb Intake",
    bgCheck: "BG Check",
    note: "Note",
    deviceEvent: "Device Event",
    basalInjection: "Long-acting injection",
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
        return record.data.mgdl != null ? `${bg(record.data.mgdl)} ${bgLabel()}` : "\u2014";
      case "note":
        return record.data.text
          ? record.data.text.length > 40
            ? record.data.text.slice(0, 40) + "\u2026"
            : record.data.text
          : "\u2014";
      case "deviceEvent":
        return record.data.eventType ?? "\u2014";
      case "basalInjection":
        return formatNumber(record.data.units, "U");
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
      case "carbs":
        return "\u2014";
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
      case "basalInjection":
        return record.data.insulinContext?.insulinName ?? "\u2014";
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
      header: () => renderSnippet(typeFilterHeaderSnippet as any, { typeFilterOptions, selectedTypes, toggleTypeFilter, clearTypeFilter }),
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
      header: () => renderSnippet(sourceFilterHeaderSnippet as any, { uniqueSources, selectedSources, toggleSourceFilter, clearSourceFilter }),
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
        case "basalInjection":
          if (r.data.insulinContext?.insulinName) values.push(r.data.insulinContext.insulinName);
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

{#snippet typeFilterHeaderSnippet({ typeFilterOptions, selectedTypes, toggleTypeFilter, clearTypeFilter }: any)}
  <ColumnFilterPopover
    label="Type"
    options={typeFilterOptions}
    selected={selectedTypes}
    onToggle={toggleTypeFilter}
    onClear={clearTypeFilter}
  />
{/snippet}

{#snippet sourceFilterHeaderSnippet({ uniqueSources, selectedSources, toggleSourceFilter, clearSourceFilter }: any)}
  <ColumnFilterPopover
    label="Source"
    options={uniqueSources.map((source: string) => ({
      value: source,
      label: source,
    }))}
    selected={selectedSources}
    searchable={true}
    searchPlaceholder="Search sources..."
    onToggle={toggleSourceFilter}
    onClear={clearSourceFilter}
  />
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
  <DataTableToolbar
    bind:globalFilter
    {table}
    selectedCount={selectedRows.length}
    onClearSelection={clearSelection}
    onBulkDelete={handleBulkDelete}
  />

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
  <DataTablePagination
    {table}
    selectedCount={table.getFilteredSelectedRowModel().rows.length}
    totalCount={table.getFilteredRowModel().rows.length}
  />
</div>
