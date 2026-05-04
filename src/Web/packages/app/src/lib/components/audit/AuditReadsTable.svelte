<script lang="ts" module>
  import type { ReadAccessAuditDto } from "$lib/api/generated/nocturne-api-client";
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
  import * as Table from "$lib/components/ui/table";
  import {
    ArrowUpDown,
    ArrowUp,
    ArrowDown,
    ChevronUp,
  } from "lucide-svelte";
  import { SvelteSet } from "svelte/reactivity";
  import DataTableToolbar from "$lib/components/treatments/DataTableToolbar.svelte";
  import DataTablePagination from "$lib/components/treatments/DataTablePagination.svelte";
  import ColumnFilterPopover from "$lib/components/treatments/ColumnFilterPopover.svelte";

  interface Props {
    rows: ReadAccessAuditDto[];
    globalFilter: string;
  }

  let { rows, globalFilter = $bindable() }: Props = $props();

  // Table state
  let sorting = $state<SortingState>([{ id: "time", desc: true }]);
  let columnFilters = $state<ColumnFiltersState>([]);
  let columnVisibility = $state<VisibilityState>({});
  let pagination = $state<PaginationState>({ pageIndex: 0, pageSize: 50 });

  // Expandable row state
  let expandedId = $state<string | null>(null);

  // Column filter states
  let selectedEntityTypes = $state<string[]>([]);
  let selectedStatusCodes = $state<string[]>([]);

  // Compute unique entity types from data
  let uniqueEntityTypes = $derived.by(() => {
    const types = new SvelteSet<string>();
    for (const r of rows) {
      if (r.entityType) types.add(r.entityType);
    }
    return Array.from(types).sort();
  });

  // Compute unique status codes from data
  let uniqueStatusCodes = $derived.by(() => {
    const codes = new SvelteSet<string>();
    for (const r of rows) {
      if (r.statusCode !== undefined) codes.add(String(r.statusCode));
    }
    return Array.from(codes).sort();
  });

  // Datetime formatter
  const dtf = new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });

  function formatCompactDateTime(date: Date | undefined): string {
    if (!date) return "\u2014";
    return dtf.format(date instanceof Date ? date : new Date(date));
  }

  function getStatusBadgeVariant(
    code: number | undefined
  ): "default" | "secondary" | "destructive" {
    if (code === undefined) return "default";
    if (code >= 200 && code < 300) return "default";
    if (code === 404) return "secondary";
    return "destructive";
  }

  function parseQueryParameters(
    json: string | undefined
  ): Record<string, string> {
    if (!json) return {};
    try {
      return JSON.parse(json);
    } catch {
      return {};
    }
  }

  // Filter helper functions
  function toggleEntityTypeFilter(value: string) {
    if (selectedEntityTypes.includes(value)) {
      selectedEntityTypes = selectedEntityTypes.filter((t) => t !== value);
    } else {
      selectedEntityTypes = [...selectedEntityTypes, value];
    }
    applyEntityTypeFilter();
  }

  function clearEntityTypeFilter() {
    selectedEntityTypes = [];
    applyEntityTypeFilter();
  }

  function applyEntityTypeFilter() {
    const column = table.getColumn("entityType");
    if (column) {
      column.setFilterValue(
        selectedEntityTypes.length > 0 ? selectedEntityTypes : undefined
      );
    }
  }

  function toggleStatusCodeFilter(value: string) {
    if (selectedStatusCodes.includes(value)) {
      selectedStatusCodes = selectedStatusCodes.filter((s) => s !== value);
    } else {
      selectedStatusCodes = [...selectedStatusCodes, value];
    }
    applyStatusCodeFilter();
  }

  function clearStatusCodeFilter() {
    selectedStatusCodes = [];
    applyStatusCodeFilter();
  }

  function applyStatusCodeFilter() {
    const column = table.getColumn("status");
    if (column) {
      column.setFilterValue(
        selectedStatusCodes.length > 0 ? selectedStatusCodes : undefined
      );
    }
  }

  // Column definitions
  const columns: ColumnDef<ReadAccessAuditDto, unknown>[] = [
    // Time column
    {
      id: "time",
      accessorFn: (row) => row.createdAt,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Time",
        }),
      cell: ({ row }) => formatCompactDateTime(row.original.createdAt),
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.createdAt
          ? new Date(rowA.original.createdAt).getTime()
          : 0;
        const b = rowB.original.createdAt
          ? new Date(rowB.original.createdAt).getTime()
          : 0;
        return a - b;
      },
    },
    // Subject column
    {
      id: "subject",
      accessorFn: (row) =>
        row.subjectName ?? row.apiSecretHashPrefix ?? "anonymous",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Subject",
        }),
      cell: ({ row }) =>
        row.original.subjectName ??
        row.original.apiSecretHashPrefix ??
        "anonymous",
    },
    // Endpoint column
    {
      id: "endpoint",
      accessorFn: (row) => row.endpoint,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Endpoint",
        }),
      cell: ({ row }) =>
        renderSnippet(endpointCellSnippet as any, {
          endpoint: row.original.endpoint,
        }),
    },
    // Entity Type column
    {
      id: "entityType",
      accessorFn: (row) => row.entityType,
      header: () =>
        renderSnippet(entityTypeFilterHeaderSnippet as any, {
          uniqueEntityTypes,
          selectedEntityTypes,
          toggleEntityTypeFilter,
          clearEntityTypeFilter,
        }),
      cell: ({ row }) => row.original.entityType ?? "\u2014",
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(row.original.entityType ?? "");
      },
    },
    // Records column
    {
      id: "records",
      accessorFn: (row) => row.recordCount,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, {
          column,
          label: "Records",
        }),
      cell: ({ row }) =>
        row.original.recordCount !== undefined &&
        row.original.recordCount !== null
          ? String(row.original.recordCount)
          : "",
    },
    // Status column
    {
      id: "status",
      accessorFn: (row) => row.statusCode,
      header: () =>
        renderSnippet(statusFilterHeaderSnippet as any, {
          uniqueStatusCodes,
          selectedStatusCodes,
          toggleStatusCodeFilter,
          clearStatusCodeFilter,
        }),
      cell: ({ row }) =>
        renderSnippet(statusBadgeSnippet as any, {
          statusCode: row.original.statusCode,
        }),
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(String(row.original.statusCode ?? ""));
      },
    },
    // IP Address column
    {
      id: "ipAddress",
      accessorFn: (row) => row.ipAddress,
      header: "IP Address",
      cell: ({ row }) =>
        renderSnippet(ipCellSnippet as any, { ip: row.original.ipAddress }),
      enableSorting: false,
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
      const values: string[] = [];

      if (r.endpoint) values.push(r.endpoint);
      if (r.entityType) values.push(r.entityType);
      if (r.subjectName) values.push(r.subjectName);
      if (r.subjectId) values.push(r.subjectId);
      if (r.ipAddress) values.push(r.ipAddress);
      if (r.authType) values.push(r.authType);
      if (r.apiSecretHashPrefix) values.push(r.apiSecretHashPrefix);
      if (r.statusCode !== undefined) values.push(String(r.statusCode));

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
      get pagination() {
        return pagination;
      },
      get globalFilter() {
        return globalFilter;
      },
    },
  });
</script>

<!-- Snippets for cell rendering -->
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

{#snippet endpointCellSnippet({ endpoint }: { endpoint: string | undefined })}
  <span class="font-mono text-xs">{endpoint ?? "\u2014"}</span>
{/snippet}

{#snippet ipCellSnippet({ ip }: { ip: string | undefined })}
  <span class="text-xs text-muted-foreground">{ip ?? "\u2014"}</span>
{/snippet}

{#snippet statusBadgeSnippet({
  statusCode,
}: {
  statusCode: number | undefined;
})}
  <Badge variant={getStatusBadgeVariant(statusCode)}>
    {statusCode ?? "\u2014"}
  </Badge>
{/snippet}

{#snippet entityTypeFilterHeaderSnippet({
  uniqueEntityTypes,
  selectedEntityTypes,
  toggleEntityTypeFilter,
  clearEntityTypeFilter,
}: any)}
  <ColumnFilterPopover
    label="Entity Type"
    options={uniqueEntityTypes.map((t: string) => ({ value: t, label: t }))}
    selected={selectedEntityTypes}
    searchable={true}
    searchPlaceholder="Search types..."
    onToggle={toggleEntityTypeFilter}
    onClear={clearEntityTypeFilter}
  />
{/snippet}

{#snippet statusFilterHeaderSnippet({
  uniqueStatusCodes,
  selectedStatusCodes,
  toggleStatusCodeFilter,
  clearStatusCodeFilter,
}: any)}
  <ColumnFilterPopover
    label="Status"
    options={uniqueStatusCodes.map((c: string) => ({ value: c, label: c }))}
    selected={selectedStatusCodes}
    onToggle={toggleStatusCodeFilter}
    onClear={clearStatusCodeFilter}
  />
{/snippet}

{#snippet expandedDetailSnippet({ row }: { row: ReadAccessAuditDto })}
  <div class="grid grid-cols-1 gap-3 px-4 py-3 text-sm sm:grid-cols-2">
    {#if row.authType}
      <div>
        <span class="font-medium text-muted-foreground">Auth Type</span>
        <p>{row.authType}</p>
      </div>
    {/if}
    {#if row.apiSecretHashPrefix}
      <div>
        <span class="font-medium text-muted-foreground"
          >API Secret Prefix</span
        >
        <p class="font-mono text-xs">{row.apiSecretHashPrefix}...</p>
      </div>
    {/if}
    {#if row.queryParameters}
      {@const params = parseQueryParameters(row.queryParameters)}
      {#if Object.keys(params).length > 0}
        <div class="sm:col-span-2">
          <span class="font-medium text-muted-foreground"
            >Query Parameters</span
          >
          <div class="mt-1 flex flex-wrap gap-2">
            {#each Object.entries(params) as [key, value] (key)}
              <span
                class="inline-flex rounded-md bg-muted px-2 py-0.5 font-mono text-xs"
              >
                {key}:{value}
              </span>
            {/each}
          </div>
        </div>
      {/if}
    {/if}
    {#if !row.authType && !row.apiSecretHashPrefix && !row.queryParameters}
      <p class="text-muted-foreground">No additional details available.</p>
    {/if}
  </div>
{/snippet}

<!-- Table UI -->
<div class="space-y-4">
  <!-- Toolbar -->
  <DataTableToolbar
    bind:globalFilter
    {table}
    selectedCount={0}
    onClearSelection={() => {}}
  />

  <!-- Table -->
  <div class="rounded-md border">
    <Table.Root>
      <Table.Header>
        {#each table.getHeaderGroups() as headerGroup (headerGroup.id)}
          <Table.Row>
            {#each headerGroup.headers as header (header.id)}
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
            class="cursor-pointer"
            onclick={(e: MouseEvent) => {
              const target = e.target as HTMLElement;
              if (
                target.closest(
                  'button, input[type="checkbox"], [role="checkbox"]'
                )
              )
                return;
              expandedId = expandedId === row.id ? null : row.id;
            }}
          >
            {#each row.getVisibleCells() as cell (cell.id)}
              <Table.Cell class="py-2">
                <FlexRender
                  content={cell.column.columnDef.cell}
                  context={cell.getContext()}
                />
              </Table.Cell>
            {/each}
          </Table.Row>
          {#if expandedId === row.id}
            <Table.Row>
              <Table.Cell colspan={columns.length} class="bg-muted/30 p-0">
                <div class="flex items-center justify-between border-b px-4 py-1">
                  <span class="text-xs font-medium text-muted-foreground"
                    >Details</span
                  >
                  <Button
                    variant="ghost"
                    size="sm"
                    class="h-6 w-6 p-0"
                    onclick={() => (expandedId = null)}
                  >
                    <ChevronUp class="h-3 w-3" />
                  </Button>
                </div>
                {@render expandedDetailSnippet({ row: row.original })}
              </Table.Cell>
            </Table.Row>
          {/if}
        {:else}
          <Table.Row>
            <Table.Cell
              colspan={columns.length}
              class="h-24 text-center text-muted-foreground"
            >
              No audit records found.
            </Table.Cell>
          </Table.Row>
        {/each}
      </Table.Body>
    </Table.Root>
  </div>

  <!-- Pagination -->
  <DataTablePagination
    {table}
    selectedCount={0}
    totalCount={table.getFilteredRowModel().rows.length}
  />
</div>
