<script lang="ts">
  import { page } from "$app/state";

  import type { Treatment, TreatmentSummary } from "$lib/api";
  import {
    TreatmentsDataTable,
    TreatmentCategoryTabs,
    TreatmentStatsCard,
    TreatmentEditDialog,
  } from "$lib/components/treatments";
  import {
    TREATMENT_CATEGORIES,
    type TreatmentCategoryId,
    countTreatmentsByCategory,
  } from "$lib/constants/treatment-categories";

  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Alert from "$lib/components/ui/alert";
  import * as Popover from "$lib/components/ui/popover";
  import {
    Calendar,
    Filter,
    X,
    ChevronDown,
  } from "lucide-svelte";
  import {
    formatInsulinDisplay,
    formatCarbDisplay,
  } from "$lib/utils/formatting";
  import { formatDate } from "$lib/utils/formatting";
  import { toast } from "svelte-sonner";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Import remote function forms and commands
  import {
    deleteTreatmentForm,
    updateTreatment,
    bulkDeleteTreatments,
  } from "./data.remote";
  
  // Get shared date params from context (set by reports layout)
  // Default: 7 days for treatment log view
  const reportsParams = requireDateParamsContext(7);

  // Create resource with automatic layout registration
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Treatments" }
  );

  const treatments = $derived<Treatment[]>(reportsResource.current?.treatments ?? []);
  const dateRange = $derived(
    reportsResource.current?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
    }
  );

  const treatmentSummary = $derived(
    reportsResource.current?.analysis?.treatmentSummary ??
      ({
        totals: { food: { carbs: 0 }, insulin: { bolus: 0, basal: 0 } },
        treatmentCount: 0,
      } as TreatmentSummary)
  );

  // Count treatments by category for UI tabs (no insulin/carb calculations)
  const counts = $derived(countTreatmentsByCategory(treatments));

  // Get filter state from URL params (used only for initial values)
  const initialCategory = page.url.searchParams.get("category");
  const initialSearch = page.url.searchParams.get("search");
  const initialEventTypes = page.url.searchParams.get("eventTypes");
  const initialNoSource = page.url.searchParams.get("noSource") === "true";

  // State - initialized from URL params
  let activeCategory = $state<TreatmentCategoryId | "all">(
    (initialCategory as TreatmentCategoryId | "all") || "all"
  );
  let searchQuery = $state(initialSearch || "");
  let selectedEventTypes = $state<string[]>(
    initialEventTypes ? initialEventTypes.split(",") : []
  );
  let filterNoSource = $state(initialNoSource);

  // Modal states
  let showDeleteConfirm = $state(false);
  let showBulkDeleteConfirm = $state(false);
  let showEditDialog = $state(false);
  let treatmentToEdit = $state<Treatment | null>(null);
  let treatmentToDelete = $state<Treatment | null>(null);
  let treatmentsToDelete = $state<Treatment[]>([]);

  // Loading states
  let isLoading = $state(false);
  let isEditLoading = $state(false);

  // Filtered treatments based on category and search
  let filteredTreatments = $derived.by(() => {
    let filtered = treatments;

    // Apply category filter
    if (activeCategory !== "all") {
      const categoryConfig = TREATMENT_CATEGORIES[activeCategory];
      if (categoryConfig) {
        const eventTypes = categoryConfig.eventTypes as readonly string[];
        filtered = filtered.filter((t) =>
          eventTypes.includes(t.eventType || "")
        );
      }
    }

    // Apply search filter
    if (searchQuery.trim()) {
      const query = searchQuery.toLowerCase();
      filtered = filtered.filter((t) => {
        const searchable = [
          t.eventType,
          t.notes,
          t.enteredBy,
          t.reason,
          t.profile,
        ]
          .filter(Boolean)
          .join(" ")
          .toLowerCase();
        return searchable.includes(query);
      });
    }

    // Apply event type filter
    if (selectedEventTypes.length > 0) {
      filtered = filtered.filter((t) =>
        selectedEventTypes.includes(t.eventType || "")
      );
    }

    // Apply no source filter
    if (filterNoSource) {
      filtered = filtered.filter((t) => !t.enteredBy && !t.data_source);
    }

    return filtered;
  });

  // Filtered stats - just counts for filtered view
  let filteredCounts = $derived(countTreatmentsByCategory(filteredTreatments));

  // Category counts from original data
  let categoryCounts = $derived(counts.byCategoryCount);

  // Available event types for filter
  let availableEventTypes = $derived.by(() => {
    const types = new Set<string>();
    for (const t of treatments) {
      if (t.eventType) types.add(t.eventType);
    }
    return Array.from(types).sort();
  });

  // Handlers
  function handleCategoryChange(category: TreatmentCategoryId | "all") {
    activeCategory = category;
    // Update URL without navigation
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

  function toggleEventType(eventType: string) {
    if (selectedEventTypes.includes(eventType)) {
      selectedEventTypes = selectedEventTypes.filter((t) => t !== eventType);
    } else {
      selectedEventTypes = [...selectedEventTypes, eventType];
    }
  }

  function clearFilters() {
    searchQuery = "";
    selectedEventTypes = [];
    activeCategory = "all";
    filterNoSource = false;
  }

  function confirmDelete(treatment: Treatment) {
    treatmentToDelete = treatment;
    showDeleteConfirm = true;
  }

  function confirmBulkDelete(treatments: Treatment[]) {
    treatmentsToDelete = treatments;
    showBulkDeleteConfirm = true;
  }

  function editTreatment(treatment: Treatment) {
    treatmentToEdit = treatment;
    showEditDialog = true;
  }

  function handleEditClose() {
    showEditDialog = false;
    treatmentToEdit = null;
  }

  async function handleEditSave(updatedTreatment: Treatment) {
    isEditLoading = true;
    try {
      // Use the remote function command for programmatic submission
      const result = await updateTreatment({
        treatmentId: updatedTreatment._id || "",
        treatmentData: updatedTreatment,
      });

      toast.success(result.message);
      showEditDialog = false;
      treatmentToEdit = null;
      // Trigger data reload
      reportsResource.refresh();
    } catch (error) {
      console.error("Update error:", error);
      toast.error("Failed to update treatment");
    } finally {
      isEditLoading = false;
    }
  }

  // Check if any filters are active
  let hasActiveFilters = $derived(
    searchQuery.trim() !== "" ||
      selectedEventTypes.length > 0 ||
      activeCategory !== "all" ||
      filterNoSource
  );

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
      <span>{treatments.length.toLocaleString()} treatments</span>
    </div>
    <h1 class="text-center text-3xl font-bold">Treatment Log</h1>
    <p class="mx-auto max-w-2xl text-center text-muted-foreground">
      Review and manage your insulin doses, carb entries, and device events. Use
      filters to find specific treatments.
    </p>
  </div>

  <!-- Summary Stats - uses backend TreatmentSummary for accurate totals -->
  <TreatmentStatsCard {treatmentSummary} counts={filteredCounts} {dateRange} />

  <!-- Category Tabs -->
  <TreatmentCategoryTabs
    {activeCategory}
    {categoryCounts}
    onChange={handleCategoryChange}
  />

  <!-- Filters Panel -->
  <Card.Root>
    <Card.Content class="p-4">
      <div
        class="flex flex-col gap-4 md:flex-row md:items-end md:justify-between"
      >
        <!-- Left side: Search and Event Type filter -->
        <div class="flex flex-1 flex-col gap-4 md:flex-row md:items-end">
          <!-- Search -->
          <div class="flex-1 max-w-sm">
            <Label for="search" class="text-sm font-medium">Search</Label>
            <Input
              id="search"
              type="text"
              placeholder="Search treatments..."
              value={searchQuery}
              oninput={handleSearch}
            />
          </div>

          <!-- Event Type Filter -->
          <Popover.Root>
            <Popover.Trigger>
              {#snippet child({ props })}
                <Button variant="outline" class="gap-2" {...props}>
                  <Filter class="h-4 w-4" />
                  Event Types
                  {#if selectedEventTypes.length > 0}
                    <Badge variant="secondary" class="ml-1">
                      {selectedEventTypes.length}
                    </Badge>
                  {/if}
                  <ChevronDown class="h-4 w-4" />
                </Button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-64 p-3" align="start">
              <div class="space-y-2 max-h-64 overflow-y-auto">
                {#each availableEventTypes as eventType}
                  <label
                    class="flex items-center gap-2 text-sm cursor-pointer hover:bg-muted/50 p-1 rounded"
                  >
                    <input
                      type="checkbox"
                      checked={selectedEventTypes.includes(eventType)}
                      onchange={() => toggleEventType(eventType)}
                      class="rounded"
                    />
                    {eventType}
                  </label>
                {/each}
              </div>
            </Popover.Content>
          </Popover.Root>

          <!-- No Source Filter -->
          <Button
            variant={filterNoSource ? "default" : "outline"}
            class="gap-2"
            onclick={() => (filterNoSource = !filterNoSource)}
          >
            <Filter class="h-4 w-4" />
            No Source
          </Button>
        </div>

        <!-- Right side: Clear filters -->
        <div class="flex items-center gap-2">
          {#if hasActiveFilters}
            <Button variant="ghost" size="sm" onclick={clearFilters}>
              <X class="mr-1 h-4 w-4" />
              Clear filters
            </Button>
          {/if}
        </div>
      </div>

      <!-- Active filters display -->
      {#if hasActiveFilters}
        <div
          class="mt-4 flex flex-wrap items-center gap-2 pt-4 border-t text-sm"
        >
          <span class="text-muted-foreground">Showing:</span>
          <span class="font-medium">
            {filteredTreatments.length} of {treatments.length}
          </span>

          {#if activeCategory !== "all"}
            <Badge variant="secondary" class="gap-1">
              {TREATMENT_CATEGORIES[activeCategory].name}
              <button
                onclick={() => (activeCategory = "all")}
                class="ml-1 hover:text-foreground"
              >
                <X class="h-3 w-3" />
              </button>
            </Badge>
          {/if}

          {#each selectedEventTypes as eventType}
            <Badge variant="outline" class="gap-1">
              {eventType}
              <button
                onclick={() => toggleEventType(eventType)}
                class="ml-1 hover:text-foreground"
              >
                <X class="h-3 w-3" />
              </button>
            </Badge>
          {/each}

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

          {#if filterNoSource}
            <Badge variant="secondary" class="gap-1">
              No Source
              <button
                onclick={() => (filterNoSource = false)}
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
        treatments={filteredTreatments}
        onEdit={editTreatment}
        onDelete={confirmDelete}
        onBulkDelete={confirmBulkDelete}
      />
    </Card.Content>
  </Card.Root>

  <!-- Footer -->
  <div class="text-center text-xs text-muted-foreground">
    <p>
      Report generated from {treatments.length.toLocaleString()} treatments between
      {new Date(dateRange.from).toLocaleDateString()} and {new Date(
        dateRange.to
      ).toLocaleDateString()}
    </p>
  </div>
</div>

<!-- Edit Treatment Dialog -->
<TreatmentEditDialog
  bind:open={showEditDialog}
  treatment={treatmentToEdit}
  {availableEventTypes}
  isLoading={isEditLoading}
  onClose={handleEditClose}
  onSave={handleEditSave}
/>

<!-- Delete Confirmation Modal -->
{#if showDeleteConfirm && treatmentToDelete}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
    role="dialog"
    aria-modal="true"
  >
    <Card.Root class="w-full max-w-md">
      <Card.Header>
        <Card.Title>Delete Treatment</Card.Title>
        <Card.Description>
          Are you sure you want to delete this {treatmentToDelete.eventType} treatment?
          This action cannot be undone.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <Alert.Root>
          <Alert.Title>Treatment Details</Alert.Title>
          <Alert.Description>
            <div class="space-y-1 text-sm">
              <div>
                <strong>Time:</strong>
                {formatDate(treatmentToDelete.created_at)}
              </div>
              <div>
                <strong>Type:</strong>
                {treatmentToDelete.eventType || "Unknown"}
              </div>
              {#if treatmentToDelete.insulin}
                <div>
                  <strong>Insulin:</strong>
                  {formatInsulinDisplay(treatmentToDelete.insulin)}U
                </div>
              {/if}
              {#if treatmentToDelete.carbs}
                <div>
                  <strong>Carbs:</strong>
                  {formatCarbDisplay(treatmentToDelete.carbs)}g
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
            treatmentToDelete = null;
          }}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <form
          {...deleteTreatmentForm
            .for(treatmentToDelete._id || "")
            .enhance(async ({ submit }) => {
              isLoading = true;
              try {
                await submit();
                toast.success("Treatment deleted successfully");
                showDeleteConfirm = false;
                treatmentToDelete = null;
                reportsResource.refresh();
              } catch (error) {
                console.error("Delete error:", error);
                toast.error("Failed to delete treatment");
              } finally {
                isLoading = false;
              }
            })}
          style="flex: 1;"
        >
          <input
            type="hidden"
            name="treatmentId"
            value={treatmentToDelete._id}
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
{#if showBulkDeleteConfirm && treatmentsToDelete.length > 0}
  <div
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
    role="dialog"
    aria-modal="true"
  >
    <Card.Root class="w-full max-w-lg">
      <Card.Header>
        <Card.Title>Delete {treatmentsToDelete.length} Treatments</Card.Title>
        <Card.Description>
          Are you sure you want to delete {treatmentsToDelete.length} selected treatment{treatmentsToDelete.length !==
          1
            ? "s"
            : ""}? This action cannot be undone.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <Alert.Root>
          <Alert.Title>Selected Treatments</Alert.Title>
          <Alert.Description>
            <div class="max-h-48 space-y-2 overflow-y-auto text-sm">
              {#each treatmentsToDelete.slice(0, 5) as treatment}
                <div
                  class="flex items-center justify-between border-b border-border py-1 last:border-b-0"
                >
                  <div>
                    <div class="font-medium">
                      {treatment.eventType || "Unknown"}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      {formatDate(treatment.created_at)}
                    </div>
                  </div>
                  <div class="text-xs">
                    {#if treatment.insulin}
                      {formatInsulinDisplay(treatment.insulin)}U
                    {/if}
                    {#if treatment.carbs}
                      {formatCarbDisplay(treatment.carbs)}g
                    {/if}
                  </div>
                </div>
              {/each}
              {#if treatmentsToDelete.length > 5}
                <div class="py-2 text-center text-xs text-muted-foreground">
                  ... and {treatmentsToDelete.length - 5} more treatments
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
            treatmentsToDelete = [];
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
              const treatmentIds = treatmentsToDelete
                .map((t) => t._id)
                .filter(Boolean) as string[];
              const result = await bulkDeleteTreatments(treatmentIds);
              if (result.success) {
                toast.success(result.message);
                showBulkDeleteConfirm = false;
                treatmentsToDelete = [];
                reportsResource.refresh();
              } else {
                toast.error(result.message);
              }
            } catch (error) {
              console.error("Bulk delete error:", error);
              toast.error("Failed to delete treatments");
            } finally {
              isLoading = false;
            }
          }}
        >
          {isLoading
            ? "Deleting..."
            : `Delete ${treatmentsToDelete.length} Treatment${treatmentsToDelete.length !== 1 ? "s" : ""}`}
        </Button>
      </Card.Footer>
    </Card.Root>
  </div>
{/if}
{/if}
