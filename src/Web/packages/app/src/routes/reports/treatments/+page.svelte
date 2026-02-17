<script lang="ts">
  import { page } from "$app/state";

  import type { TreatmentSummary } from "$lib/api";
  import {
    TreatmentsDataTable,
    TreatmentCategoryTabs,
    TreatmentStatsCard,
  } from "$lib/components/treatments";
  import {
    mergeTreatmentRows,
    countV4Rows,
    type V4CategoryId,
    type TreatmentRow,
  } from "$lib/constants/treatment-categories";

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
    deleteTreatmentForm,
    bulkDeleteTreatments,
  } from "./data.remote";

  // Get shared date params from context (set by reports layout)
  const reportsParams = requireDateParamsContext(7);

  const reportsResource = contextResource(
    () => getTreatmentsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Treatments" }
  );

  const boluses = $derived(reportsResource.current?.boluses ?? []);
  const carbIntakes = $derived(reportsResource.current?.carbIntakes ?? []);
  const allRows = $derived(mergeTreatmentRows(boluses, carbIntakes));
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

  const counts = $derived(countV4Rows(allRows));

  // State
  const initialCategory = page.url.searchParams.get("category");
  const initialSearch = page.url.searchParams.get("search");

  let activeCategory = $state<V4CategoryId | "all">(
    (initialCategory as V4CategoryId | "all") || "all"
  );
  let searchQuery = $state(initialSearch || "");

  // Modal states
  let showDeleteConfirm = $state(false);
  let showBulkDeleteConfirm = $state(false);
  let rowToDelete = $state<TreatmentRow | null>(null);
  let rowsToDelete = $state<TreatmentRow[]>([]);

  // Loading states
  let isLoading = $state(false);

  // Filtered rows based on category and search
  let filteredRows = $derived.by(() => {
    let filtered = allRows;

    // Apply category filter
    if (activeCategory === "bolus") {
      filtered = filtered.filter((r) => r.kind === "bolus");
    } else if (activeCategory === "carbs") {
      filtered = filtered.filter((r) => r.kind === "carbIntake");
    }

    // Apply search filter
    if (searchQuery.trim()) {
      const query = searchQuery.toLowerCase();
      filtered = filtered.filter((r) => {
        const searchable = [
          r.kind === "bolus" ? "bolus" : "carb intake",
          r.kind === "bolus" ? r.bolusType : r.foodType,
          r.dataSource,
          r.app,
          r.device,
        ]
          .filter(Boolean)
          .join(" ")
          .toLowerCase();
        return searchable.includes(query);
      });
    }

    return filtered;
  });

  let filteredCounts = $derived(countV4Rows(filteredRows));

  // Handlers
  function handleCategoryChange(category: V4CategoryId | "all") {
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

  function confirmDelete(row: TreatmentRow) {
    rowToDelete = row;
    showDeleteConfirm = true;
  }

  function confirmBulkDelete(rows: TreatmentRow[]) {
    rowsToDelete = rows;
    showBulkDeleteConfirm = true;
  }

  let hasActiveFilters = $derived(
    searchQuery.trim() !== "" || activeCategory !== "all"
  );

  function getRowLabel(row: TreatmentRow): string {
    return row.kind === "bolus" ? "bolus" : "carb intake";
  }

  function formatRowTime(row: TreatmentRow): string {
    if (!row.mills) return "Unknown";
    return formatDateTimeCompact(new Date(row.mills).toISOString());
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
      Review and manage your insulin doses, carb entries, and device events. Use
      filters to find specific treatments.
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
              {activeCategory === "bolus" ? "Bolus" : "Carbs"}
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
                {rowToDelete.kind === "bolus" ? "Bolus" : "Carb Intake"}
              </div>
              {#if rowToDelete.kind === "bolus" && rowToDelete.insulin}
                <div>
                  <strong>Insulin:</strong>
                  {formatInsulinDisplay(rowToDelete.insulin)}U
                </div>
              {/if}
              {#if rowToDelete.kind === "carbIntake" && rowToDelete.carbs}
                <div>
                  <strong>Carbs:</strong>
                  {formatCarbDisplay(rowToDelete.carbs)}g
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
            showDeleteConfirm = false;
            rowToDelete = null;
          }}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <form
          {...deleteTreatmentForm
            .for(rowToDelete.id || "")
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
            name="treatmentId"
            value={rowToDelete.id}
          />
          <input
            type="hidden"
            name="treatmentKind"
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
                      {row.kind === "bolus" ? "Bolus" : "Carb Intake"}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      {formatRowTime(row)}
                    </div>
                  </div>
                  <div class="text-xs">
                    {#if row.kind === "bolus" && row.insulin}
                      {formatInsulinDisplay(row.insulin)}U
                    {/if}
                    {#if row.kind === "carbIntake" && row.carbs}
                      {formatCarbDisplay(row.carbs)}g
                    {/if}
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
                .map((r) => ({ id: r.id!, kind: r.kind }));
              const result = await bulkDeleteTreatments(items);
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
