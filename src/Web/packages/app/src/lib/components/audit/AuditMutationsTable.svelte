<script lang="ts" module>
  import type { MutationAuditDto } from "$lib/api/generated/nocturne-api-client";
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
    ChevronDown,
  } from "lucide-svelte";
  import { SvelteSet } from "svelte/reactivity";
  import DataTableToolbar from "$lib/components/treatments/DataTableToolbar.svelte";
  import DataTablePagination from "$lib/components/treatments/DataTablePagination.svelte";
  import ColumnFilterPopover from "$lib/components/treatments/ColumnFilterPopover.svelte";

  interface Props {
    rows: MutationAuditDto[];
    globalFilter: string;
  }

  let { rows, globalFilter = $bindable() }: Props = $props();

  // Table state
  let sorting = $state<SortingState>([{ id: "time", desc: true }]);
  let columnFilters = $state<ColumnFiltersState>([]);
  let columnVisibility = $state<VisibilityState>({});
  let pagination = $state<PaginationState>({ pageIndex: 0, pageSize: 50 });

  // Expanded row
  let expandedId = $state<string | null>(null);

  // Column filter states
  let selectedActions = $state<string[]>([]);
  let selectedEntityTypes = $state<string[]>([]);

  // Static action filter options
  const actionFilterOptions = [
    { value: "Create", label: "Create" },
    { value: "Update", label: "Update" },
    { value: "Delete", label: "Delete" },
    { value: "Restore", label: "Restore" },
  ];

  // Compute unique entity types from data
  let uniqueEntityTypes = $derived.by(() => {
    const types = new SvelteSet<string>();
    for (const r of rows) {
      if (r.entityType) types.add(r.entityType);
    }
    return Array.from(types).sort();
  });

  // Format functions
  const compactDateFormatter = new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });

  function formatCompactDate(date: Date | undefined): string {
    if (!date) return "\u2014";
    return compactDateFormatter.format(date instanceof Date ? date : new Date(date));
  }

  function truncateId(id: string): string {
    if (id.length <= 8) return id;
    return id.slice(0, 8) + "\u2026";
  }

  function getActionBadgeVariant(action: string | undefined): "default" | "secondary" | "destructive" {
    switch (action?.toLowerCase()) {
      case "delete":
        return "destructive";
      case "create":
        return "default";
      default:
        return "secondary";
    }
  }

  interface ChangeDiff {
    field: string;
    oldValue: string;
    newValue: string;
  }

  function parseChanges(changes: string | undefined): ChangeDiff[] {
    if (!changes) return [];
    try {
      const parsed = JSON.parse(changes);
      if (Array.isArray(parsed)) {
        return parsed.map((c: { field?: string; oldValue?: unknown; newValue?: unknown }) => ({
          field: c.field ?? "unknown",
          oldValue: String(c.oldValue ?? ""),
          newValue: String(c.newValue ?? ""),
        }));
      }
      if (typeof parsed === "object" && parsed !== null) {
        return Object.entries(parsed).map(([field, value]: [string, unknown]) => {
          if (typeof value === "object" && value !== null && "old" in value && "new" in value) {
            const v = value as { old: unknown; new: unknown };
            return { field, oldValue: String(v.old ?? ""), newValue: String(v.new ?? "") };
          }
          return { field, oldValue: "", newValue: String(value ?? "") };
        });
      }
      return [];
    } catch {
      return [];
    }
  }

  // Column definitions
  const columns: ColumnDef<MutationAuditDto, unknown>[] = [
    // Time column
    {
      id: "time",
      accessorFn: (row) => row.createdAt,
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Time" }),
      cell: ({ row }) => formatCompactDate(row.original.createdAt),
      sortingFn: (rowA, rowB) => {
        const a = rowA.original.createdAt ? new Date(rowA.original.createdAt).getTime() : 0;
        const b = rowB.original.createdAt ? new Date(rowB.original.createdAt).getTime() : 0;
        return a - b;
      },
    },
    // Subject column
    {
      id: "subject",
      accessorFn: (row) => row.subjectName ?? row.subjectId ?? "system",
      header: ({ column }) =>
        renderSnippet(sortableHeaderSnippet as any, { column, label: "Subject" }),
      cell: ({ row }) => {
        const r = row.original;
        return r.subjectName ?? truncateId(r.subjectId ?? "system");
      },
    },
    // Action column
    {
      id: "action",
      accessorFn: (row) => row.action,
      header: () =>
        renderSnippet(actionFilterHeaderSnippet as any, {
          actionFilterOptions,
          selectedActions,
          toggleActionFilter,
          clearActionFilter,
        }),
      cell: ({ row }) =>
        renderSnippet(actionBadgeSnippet as any, { action: row.original.action }),
      filterFn: (row, _id, filterValue: string[]) => {
        if (!filterValue.length) return true;
        return filterValue.includes(row.original.action ?? "");
      },
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
    // Entity ID column
    {
      id: "entityId",
      accessorFn: (row) => row.entityId,
      header: "Entity ID",
      cell: ({ row }) =>
        renderSnippet(entityIdSnippet as any, { entityId: row.original.entityId }),
      enableSorting: false,
    },
    // Endpoint column
    {
      id: "endpoint",
      accessorFn: (row) => row.endpoint,
      header: "Endpoint",
      cell: ({ row }) =>
        renderSnippet(mutedTextSnippet as any, { text: row.original.endpoint }),
      enableSorting: false,
    },
    // IP Address column
    {
      id: "ipAddress",
      accessorFn: (row) => row.ipAddress,
      header: "IP Address",
      cell: ({ row }) =>
        renderSnippet(mutedTextSnippet as any, { text: row.original.ipAddress }),
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

      if (r.action) values.push(r.action);
      if (r.entityType) values.push(r.entityType);
      if (r.subjectName) values.push(r.subjectName);
      if (r.subjectId) values.push(r.subjectId);
      if (r.endpoint) values.push(r.endpoint);
      if (r.ipAddress) values.push(r.ipAddress);
      if (r.entityId) values.push(r.entityId);
      if (r.reason) values.push(r.reason);

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

  // Filter helper functions
  function toggleActionFilter(action: string) {
    if (selectedActions.includes(action)) {
      selectedActions = selectedActions.filter((a) => a !== action);
    } else {
      selectedActions = [...selectedActions, action];
    }
    applyActionFilter();
  }

  function clearActionFilter() {
    selectedActions = [];
    applyActionFilter();
  }

  function applyActionFilter() {
    const column = table.getColumn("action");
    if (column) {
      column.setFilterValue(
        selectedActions.length > 0 ? selectedActions : undefined
      );
    }
  }

  function toggleEntityTypeFilter(entityType: string) {
    if (selectedEntityTypes.includes(entityType)) {
      selectedEntityTypes = selectedEntityTypes.filter((t) => t !== entityType);
    } else {
      selectedEntityTypes = [...selectedEntityTypes, entityType];
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

  function toggleExpanded(id: string | undefined) {
    if (!id) return;
    expandedId = expandedId === id ? null : id;
  }
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

{#snippet actionBadgeSnippet({ action }: { action: string | undefined })}
  <Badge variant={getActionBadgeVariant(action)}>
    {action ?? "\u2014"}
  </Badge>
{/snippet}

{#snippet actionFilterHeaderSnippet({ actionFilterOptions, selectedActions, toggleActionFilter, clearActionFilter }: any)}
  <ColumnFilterPopover
    label="Action"
    options={actionFilterOptions}
    selected={selectedActions}
    onToggle={toggleActionFilter}
    onClear={clearActionFilter}
  />
{/snippet}

{#snippet entityTypeFilterHeaderSnippet({ uniqueEntityTypes, selectedEntityTypes, toggleEntityTypeFilter, clearEntityTypeFilter }: any)}
  <ColumnFilterPopover
    label="Entity Type"
    options={uniqueEntityTypes.map((type: string) => ({
      value: type,
      label: type,
    }))}
    selected={selectedEntityTypes}
    searchable={true}
    searchPlaceholder="Search entity types..."
    onToggle={toggleEntityTypeFilter}
    onClear={clearEntityTypeFilter}
  />
{/snippet}

{#snippet entityIdSnippet({ entityId }: { entityId: string | undefined })}
  <span class="font-mono text-xs">
    {entityId ? truncateId(entityId) : "\u2014"}
  </span>
{/snippet}

{#snippet mutedTextSnippet({ text }: { text: string | undefined })}
  <span class="text-xs text-muted-foreground">
    {text ?? "\u2014"}
  </span>
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
              if (target.closest('button, input[type="checkbox"], [role="checkbox"]')) return;
              toggleExpanded(row.original.id);
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
            <Table.Cell class="w-8 py-2">
              {#if expandedId === row.original.id}
                <ChevronUp class="h-4 w-4 text-muted-foreground" />
              {:else}
                <ChevronDown class="h-4 w-4 text-muted-foreground" />
              {/if}
            </Table.Cell>
          </Table.Row>
          <!-- Expanded detail row -->
          {#if expandedId === row.original.id}
            <Table.Row class="bg-muted/50 hover:bg-muted/50">
              <Table.Cell colspan={columns.length + 1} class="p-4">
                <div class="space-y-3 text-sm">
                  {#if row.original.reason}
                    <div>
                      <span class="font-medium text-muted-foreground">Reason:</span>
                      <span class="ml-2">{row.original.reason}</span>
                    </div>
                  {/if}
                  {#if row.original.authType}
                    <div>
                      <span class="font-medium text-muted-foreground">Auth Type:</span>
                      <span class="ml-2">{row.original.authType}</span>
                    </div>
                  {/if}
                  {#if row.original.entityId}
                    <div>
                      <span class="font-medium text-muted-foreground">Full Entity ID:</span>
                      <span class="ml-2 font-mono text-xs">{row.original.entityId}</span>
                    </div>
                  {/if}
                  {#if row.original.changes}
                    {@const diffs = parseChanges(row.original.changes)}
                    {#if diffs.length > 0}
                      <div>
                        <span class="font-medium text-muted-foreground">Changes:</span>
                        <div class="mt-2 space-y-1.5">
                          {#each diffs as diff (diff.field)}
                            <div class="flex items-baseline gap-2 rounded bg-background px-3 py-1.5 font-mono text-xs">
                              <span class="font-medium text-foreground">{diff.field}</span>
                              {#if diff.oldValue}
                                <span class="text-destructive line-through">{diff.oldValue}</span>
                                <span class="text-muted-foreground">&rarr;</span>
                              {/if}
                              <span class="text-green-600 dark:text-green-400">{diff.newValue}</span>
                            </div>
                          {/each}
                        </div>
                      </div>
                    {:else}
                      <div>
                        <span class="font-medium text-muted-foreground">Changes (raw):</span>
                        <pre class="mt-1 whitespace-pre-wrap rounded bg-background p-2 font-mono text-xs">{row.original.changes}</pre>
                      </div>
                    {/if}
                  {/if}
                  {#if !row.original.reason && !row.original.authType && !row.original.changes}
                    <span class="text-muted-foreground">No additional details available.</span>
                  {/if}
                </div>
              </Table.Cell>
            </Table.Row>
          {/if}
        {:else}
          <Table.Row>
            <Table.Cell
              colspan={columns.length + 1}
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
