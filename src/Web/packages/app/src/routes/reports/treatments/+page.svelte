<script lang="ts">
  import { page } from "$app/state";

  import type { TreatmentSummary } from "$lib/api";
  import {
    TreatmentsDataTable,
    TreatmentCategoryTabs,
    TreatmentStatsCard,
  } from "$lib/components/treatments";
  import {
    mergeEntryRecords,
    countEntryRecords,
    type EntryCategoryId,
    type EntryRecord,
    ENTRY_CATEGORIES,
  } from "$lib/constants/entry-categories";

  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Alert from "$lib/components/ui/alert";
  import { Calendar, X } from "lucide-svelte";
  import {
    formatInsulinDisplay,
    formatCarbDisplay,
    formatDateTimeCompact,
  } from "$lib/utils/formatting";
  import { toast } from "svelte-sonner";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Import remote function forms and commands
  import {
    getTreatmentsData,
    deleteEntryForm,
    bulkDeleteEntries,
  } from "./data.remote";

  // Get shared date params from context (set by reports layout)
  const reportsParams = requireDateParamsContext(7);

  const reportsResource = contextResource(
    () => getTreatmentsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Treatments" }
  );

  const allRows = $derived(
    mergeEntryRecords({
      boluses: reportsResource.current?.boluses,
      carbIntakes: reportsResource.current?.carbIntakes,
      bgChecks: reportsResource.current?.bgChecks,
      notes: reportsResource.current?.notes,
      deviceEvents: reportsResource.current?.deviceEvents,
    })
  );
  const dateRange = $derived(
    reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
    }
  );

  const treatmentSummary = $derived(
    reportsResource.current?.treatmentSummary ??
      ({
        totals: { food: { carbs: 0 }, insulin: { bolus: 0, basal: 0 } },
        treatmentCount: 0,
      } as TreatmentSummary)
  );

  const counts = $derived(countEntryRecords(allRows));

  // State
  const initialCategory = page.url.searchParams.get("category");
  const initialSearch = page.url.searchParams.get("search");

  let activeCategory = $state<EntryCategoryId | "all">(
    (initialCategory as EntryCategoryId | "all") || "all"
  );
  let searchQuery = $state(initialSearch || "");

  // Modal states
  let showDeleteConfirm = $state(false);
  let showBulkDeleteConfirm = $state(false);
  let rowToDelete = $state<EntryRecord | null>(null);
  let rowsToDelete = $state<EntryRecord[]>([]);

  // Loading states
  let isLoading = $state(false);

  // Filtered rows based on category and search
  let filteredRows = $derived.by(() => {
    let filtered = allRows;

    // Apply category filter
    if (activeCategory !== "all") {
      filtered = filtered.filter((r) => r.kind === activeCategory);
    }

    // Apply search filter
    if (searchQuery.trim()) {
      const query = searchQuery.toLowerCase();
      filtered = filtered.filter((r) => {
        const searchable: string[] = [ENTRY_CATEGORIES[r.kind].name];

        switch (r.kind) {
          case "bolus":
            if (r.data.bolusType) searchable.push(r.data.bolusType);
            break;
          case "carbs":
            if (r.data.foodType) searchable.push(r.data.foodType);
            break;
          case "bgCheck":
            if (r.data.glucoseType) searchable.push(r.data.glucoseType);
            break;
          case "note":
            if (r.data.text) searchable.push(r.data.text);
            if (r.data.eventType) searchable.push(r.data.eventType);
            break;
          case "deviceEvent":
            if (r.data.eventType) searchable.push(r.data.eventType);
            if (r.data.notes) searchable.push(r.data.notes);
            break;
        }

        if (r.data.dataSource) searchable.push(r.data.dataSource);
        if (r.data.app) searchable.push(r.data.app);
        if (r.data.device) searchable.push(r.data.device);

        return searchable.join(" ").toLowerCase().includes(query);
      });
    }

    return filtered;
  });

  let filteredCounts = $derived(countEntryRecords(filteredRows));

  // Handlers
  function handleCategoryChange(category: EntryCategoryId | "all") {
    activeCategory = category;
    const url = new URL(window.location.href);
    if (category === "all") {
      url.searchParams.delete("category");
    } else {
      url.searchParams.set("category", category);
    }
    window.history.replaceState({}, "", url);
  }

  function handleSearch(e: Event) {
    const target = e.target as HTMLInputElement;
    searchQuery = target.value;
  }

  function clearFilters() {
    searchQuery = "";
    activeCategory = "all";
  }

  function confirmDelete(row: EntryRecord) {
    rowToDelete = row;
    showDeleteConfirm = true;
  }

  function confirmBulkDelete(rows: EntryRecord[]) {
    rowsToDelete = rows;
    showBulkDeleteConfirm = true;
  }

  let hasActiveFilters = $derived(
    searchQuery.trim() !== "" || activeCategory !== "all"
  );

  function getRowLabel(record: EntryRecord): string {
    return ENTRY_CATEGORIES[record.kind].name.toLowerCase();
  }

  function formatRowTime(record: EntryRecord): string {
    if (!record.data.mills) return "Unknown";
    return formatDateTimeCompact(new Date(record.data.mills).toISOString());
  }

  function getDeleteDescription(record: EntryRecord): string {
    switch (record.kind) {
      case "bolus":
        return record.data.insulin
          ? `${formatInsulinDisplay(record.data.insulin)}U`
          : "Bolus";
      case "carbs":
        return record.data.carbs
          ? `${formatCarbDisplay(record.data.carbs)}g`
          : "Carb Intake";
      case "bgCheck":
        return record.data.mgdl
          ? `${record.data.mgdl} mg/dL`
          : "BG Check";
      case "note":
        return record.data.text
          ? record.data.text.length > 50
            ? record.data.text.slice(0, 50) + "..."
            : record.data.text
          : "Note";
      case "deviceEvent":
        return record.data.eventType ?? "Device Event";
    }
  }
</script>

<svelte:head>
  <title>Treatment Log - Nocturne</title>
  <meta
    name="description"
    content="View and manage your diabetes treatments, insulin doses, and carb entries"
  />
</svelte:head>

{#if reportsResource.current}
<div class="container mx-auto space-y-6 px-4 py-6">
  <!-- Header -->
  <div class="space-y-2">
    <div
      class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
    >
      <Calendar class="h-4 w-4" />
      <span>
        {new Date(dateRange.from).toLocaleDateString()} – {new Date(
          dateRange.to
        ).toLocaleDateString()}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{allRows.length.toLocaleString()} records</span>
    </div>
    <h1 class="text-center text-3xl font-bold">Treatment Log</h1>
    <p class="mx-auto max-w-2xl text-center text-muted-foreground">
      Review and manage your insulin doses, carb entries, BG checks, notes, and
      device events. Use filters to find specific records.
    </p>
  </div>

  <!-- Summary Stats -->
  <TreatmentStatsCard {treatmentSummary} counts={filteredCounts} {dateRange} />

  <!-- Category Tabs -->
  <TreatmentCategoryTabs
    {activeCategory}
    categoryCounts={counts}
    onChange={handleCategoryChange}
  />

  <!-- Filters Panel -->
  <Card.Root>
    <Card.Content class="p-4">
      <div
        class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between"
      >
        <div class="flex flex-1 flex-col gap-4 md:flex-row md:items-end">
          <div class="flex-1 max-w-sm">
            <Label for="search" class="text-sm font-medium">Search</Label>
            <Input
              id="search"
              type="text"
              placeholder="Search records..."
              value={searchQuery}
              oninput={handleSearch}
            />
          </div>
        </div>

        <div class="flex items-center gap-2">
          {#if hasActiveFilters}
            <Button variant="ghost" size="sm" onclick={clearFilters}>
              <X class="mr-1 h-4 w-4" />
              Clear filters
            </Button>
          {/if}
        </div>
      </div>

      {#if hasActiveFilters}
        <div
          class="mt-4 flex flex-wrap items-center gap-2 pt-4 border-t text-sm"
        >
          <span class="text-muted-foreground">Showing:</span>
          <span class="font-medium">
            {filteredRows.length} of {allRows.length}
          </span>

          {#if activeCategory !== "all"}
            <Badge variant="secondary" class="gap-1">
              {ENTRY_CATEGORIES[activeCategory].name}
              <button
                onclick={() => (activeCategory = "all")}
                class="ml-1 hover:text-foreground"
              >
                <X class="h-3 w-3" />
              </button>
            </Badge>
          {/if}

          {#if searchQuery.trim()}
            <Badge variant="outline" class="gap-1">
              "{searchQuery}"
              <button
                onclick={() => (searchQuery = "")}
                class="ml-1 hover:text-foreground"
              >
                <X class="h-3 w-3" />
              </button>
            </Badge>
          {/if}
        </div>
      {/if}
    </Card.Content>
  </Card.Root>

  <!-- Data Table -->
  <Card.Root>
    <Card.Content class="p-0">
      <TreatmentsDataTable
        rows={filteredRows}
        onDelete={confirmDelete}
        onBulkDelete={confirmBulkDelete}
      />
    </Card.Content>
  </Card.Root>

  <!-- Footer -->
  <div class="text-center text-xs text-muted-foreground">
    <p>
      Report generated from {allRows.length.toLocaleString()} records between
      {new Date(dateRange.from).toLocaleDateString()} and {new Date(
        dateRange.to
      ).toLocaleDateString()}
    </p>
  </div>
</div>

<!-- Delete Confirmation Modal -->
{#if showDeleteConfirm && rowToDelete}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
    role="dialog"
    aria-modal="true"
  >
    <Card.Root class="w-full max-w-md">
      <Card.Header>
        <Card.Title>Delete {getRowLabel(rowToDelete)}</Card.Title>
        <Card.Description>
          Are you sure you want to delete this {getRowLabel(rowToDelete)}?
          This action cannot be undone.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <Alert.Root>
          <Alert.Title>Details</Alert.Title>
          <Alert.Description>
            <div class="space-y-1 text-sm">
              <div>
                <strong>Time:</strong>
                {formatRowTime(rowToDelete)}
              </div>
              <div>
                <strong>Type:</strong>
                {ENTRY_CATEGORIES[rowToDelete.kind].name}
              </div>
              <div>
                <strong>Value:</strong>
                {getDeleteDescription(rowToDelete)}
              </div>
            </div>
          </Alert.Description>
        </Alert.Root>
      </Card.Content>

      <Card.Footer class="flex gap-3">
        <Button
          type="button"
          variant="secondary"
          class="flex-1"
          onclick={() => {
            showDeleteConfirm = false;
            rowToDelete = null;
          }}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <form
          {...deleteEntryForm
            .for(rowToDelete.data.id || "")
            .enhance(async ({ submit }) => {
              isLoading = true;
              try {
                await submit();
                toast.success("Deleted successfully");
                showDeleteConfirm = false;
                rowToDelete = null;
                reportsResource.refresh();
              } catch (error) {
                console.error("Delete error:", error);
                toast.error("Failed to delete");
              } finally {
                isLoading = false;
              }
            })}
          style="flex: 1;"
        >
          <input
            type="hidden"
            name="entryId"
            value={rowToDelete.data.id}
          />
          <input
            type="hidden"
            name="entryKind"
            value={rowToDelete.kind}
          />
          <Button
            type="submit"
            variant="destructive"
            class="w-full"
            disabled={isLoading}
          >
            {isLoading ? "Deleting..." : "Delete"}
          </Button>
        </form>
      </Card.Footer>
    </Card.Root>
  </div>
{/if}

<!-- Bulk Delete Confirmation Modal -->
{#if showBulkDeleteConfirm && rowsToDelete.length > 0}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
    role="dialog"
    aria-modal="true"
  >
    <Card.Root class="w-full max-w-lg">
      <Card.Header>
        <Card.Title>Delete {rowsToDelete.length} Records</Card.Title>
        <Card.Description>
          Are you sure you want to delete {rowsToDelete.length} selected record{rowsToDelete.length !==
          1
            ? "s"
            : ""}? This action cannot be undone.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <Alert.Root>
          <Alert.Title>Selected Records</Alert.Title>
          <Alert.Description>
            <div class="max-h-48 space-y-2 overflow-y-auto text-sm">
              {#each rowsToDelete.slice(0, 5) as row}
                <div
                  class="flex items-center justify-between border-b border-border py-1 last:border-b-0"
                >
                  <div>
                    <div class="font-medium">
                      {ENTRY_CATEGORIES[row.kind].name}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      {formatRowTime(row)}
                    </div>
                  </div>
                  <div class="text-xs">
                    {getDeleteDescription(row)}
                  </div>
                </div>
              {/each}
              {#if rowsToDelete.length > 5}
                <div class="py-2 text-center text-xs text-muted-foreground">
                  ... and {rowsToDelete.length - 5} more records
                </div>
              {/if}
            </div>
          </Alert.Description>
        </Alert.Root>
      </Card.Content>

      <Card.Footer class="flex gap-3">
        <Button
          type="button"
          variant="secondary"
          class="flex-1"
          onclick={() => {
            showBulkDeleteConfirm = false;
            rowsToDelete = [];
          }}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <Button
          type="button"
          variant="destructive"
          class="flex-1"
          disabled={isLoading}
          onclick={async () => {
            isLoading = true;
            try {
              const items = rowsToDelete
                .map((r) => ({ id: r.data.id!, kind: r.kind }));
              const result = await bulkDeleteEntries(items);
              if (result.success) {
                toast.success(result.message);
                showBulkDeleteConfirm = false;
                rowsToDelete = [];
                reportsResource.refresh();
              } else {
                toast.error(result.message);
              }
            } catch (error) {
              console.error("Bulk delete error:", error);
              toast.error("Failed to delete records");
            } finally {
              isLoading = false;
            }
          }}
        >
          {isLoading
            ? "Deleting..."
            : `Delete ${rowsToDelete.length} Record${rowsToDelete.length !== 1 ? "s" : ""}`}
        </Button>
      </Card.Footer>
    </Card.Root>
  </div>
{/if}
{/if}
