<script lang="ts">
  import { enhance } from "$app/forms";
  import { page } from "$app/state";

  import type { Treatment } from "$lib/api";
  import {
    TreatmentsDataTable,
    TreatmentCategoryTabs,
    TreatmentStatsCard,
    TreatmentEditDialog,
  } from "$lib/components/treatments";
  import {
    TREATMENT_CATEGORIES,
    type TreatmentCategoryId,
    calculateTreatmentStats,
  } from "$lib/constants/treatment-categories";

  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Alert from "$lib/components/ui/alert";
  import * as Popover from "$lib/components/ui/popover";
  import { Calendar, Filter, X, ChevronDown } from "lucide-svelte";
  import {
    formatInsulinDisplay,
    formatCarbDisplay,
  } from "$lib/utils/calculate/treatment-stats.js";
  import { formatDateTime } from "$lib/utils/date-formatting";

  let { data, form } = $props();

  // Compute stats from layout data
  const stats = $derived(
    calculateTreatmentStats(data.treatments as Treatment[])
  );

  // Get filter state from URL params
  const categoryParam = $derived(page.url.searchParams.get("category"));
  const searchParam = $derived(page.url.searchParams.get("search"));
  const eventTypesParam = $derived(page.url.searchParams.get("eventTypes"));

  // State
  let activeCategory = $state<TreatmentCategoryId | "all">(
    (categoryParam as TreatmentCategoryId | "all") || "all"
  );
  let searchQuery = $state(searchParam || "");
  let selectedEventTypes = $state<string[]>(
    eventTypesParam ? eventTypesParam.split(",") : []
  );

  // Modal states
  let showDeleteConfirm = $state(false);
  let showBulkDeleteConfirm = $state(false);
  let showEditDialog = $state(false);
  let treatmentToEdit = $state<Treatment | null>(null);
  let treatmentToDelete = $state<Treatment | null>(null);
  let treatmentsToDelete = $state<Treatment[]>([]);
  let isLoading = $state(false);
  let isEditLoading = $state(false);
  $inspect(data);

  // Hidden form element reference for edit submission
  let editFormElement = $state<HTMLFormElement | null>(null);

  // Status message
  let statusMessage = $state<{
    type: "success" | "error";
    text: string;
  } | null>(null);

  // Handle form results
  $effect(() => {
    if (form?.message) {
      showStatus("success", form.message);

      // Handle delete result
      if (form.deletedTreatmentId) {
        showDeleteConfirm = false;
        treatmentToDelete = null;
      }

      // Handle bulk delete result
      if (form.deletedTreatmentIds) {
        showBulkDeleteConfirm = false;
        treatmentsToDelete = [];
      }

      // Handle edit result
      if (form.updatedTreatment) {
        showEditDialog = false;
        treatmentToEdit = null;
        isEditLoading = false;
      }
    } else if (form?.error) {
      showStatus("error", form.error);
      isEditLoading = false;
    }
  });

  // Filtered treatments based on category and search
  let filteredTreatments = $derived.by(() => {
    let filtered = data.treatments as Treatment[];

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

    return filtered;
  });

  // Filtered stats
  let filteredStats = $derived(calculateTreatmentStats(filteredTreatments));

  // Category counts from original data
  let categoryCounts = $derived(stats.byCategoryCount);

  // Available event types for filter
  let availableEventTypes = $derived.by(() => {
    const types = new Set<string>();
    for (const t of data.treatments as Treatment[]) {
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

  function handleEditSave(updatedTreatment: Treatment) {
    if (!editFormElement) return;

    // Set hidden form values and submit
    const treatmentIdInput = editFormElement.querySelector(
      'input[name="treatmentId"]'
    ) as HTMLInputElement;
    const treatmentDataInput = editFormElement.querySelector(
      'input[name="treatmentData"]'
    ) as HTMLInputElement;

    if (treatmentIdInput && treatmentDataInput) {
      treatmentIdInput.value = updatedTreatment._id || "";
      treatmentDataInput.value = JSON.stringify(updatedTreatment);
      isEditLoading = true;
      editFormElement.requestSubmit();
    }
  }

  function showStatus(type: "success" | "error", text: string) {
    statusMessage = { type, text };
    setTimeout(() => {
      statusMessage = null;
    }, 5000);
  }

  // Check if any filters are active
  let hasActiveFilters = $derived(
    searchQuery.trim() !== "" ||
      selectedEventTypes.length > 0 ||
      activeCategory !== "all"
  );
</script>

<svelte:head>
  <title>Treatment Log - Nocturne</title>
  <meta
    name="description"
    content="View and manage your diabetes treatments, insulin doses, and carb entries"
  />
</svelte:head>

<div class="container mx-auto space-y-6 px-4 py-6">
  <!-- Header -->
  <div class="space-y-2">
    <div
      class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
    >
      <Calendar class="h-4 w-4" />
      <span>
        {new Date(data.dateRange.from).toLocaleDateString()} – {new Date(
          data.dateRange.to
        ).toLocaleDateString()}
      </span>
      <span class="text-muted-foreground/50">•</span>
      <span>{data.treatments.length.toLocaleString()} treatments</span>
    </div>
    <h1 class="text-center text-3xl font-bold">Treatment Log</h1>
    <p class="mx-auto max-w-2xl text-center text-muted-foreground">
      Review and manage your insulin doses, carb entries, and device events. Use
      filters to find specific treatments.
    </p>
  </div>

  <!-- Status Messages -->
  {#if statusMessage}
    <Alert.Root
      variant={statusMessage.type === "error" ? "destructive" : "default"}
    >
      <Alert.Description class="flex items-center justify-between">
        <span>{statusMessage.text}</span>
        <Button
          variant="ghost"
          size="sm"
          onclick={() => (statusMessage = null)}
        >
          <X class="h-4 w-4" />
        </Button>
      </Alert.Description>
    </Alert.Root>
  {/if}

  <!-- Summary Stats -->
  <TreatmentStatsCard stats={filteredStats} dateRange={data.dateRange} />

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
            {filteredTreatments.length} of {data.treatments.length}
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
      Report generated from {data.treatments.length.toLocaleString()} treatments between
      {new Date(data.dateRange.from).toLocaleDateString()} and {new Date(
        data.dateRange.to
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

<!-- Hidden form for edit submission -->
<form
  bind:this={editFormElement}
  method="POST"
  action="?/updateTreatment"
  class="hidden"
  use:enhance={() => {
    return async ({ update }) => {
      await update();
    };
  }}
>
  <input type="hidden" name="treatmentId" value="" />
  <input type="hidden" name="treatmentData" value="" />
</form>

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
          method="POST"
          action="?/deleteTreatment"
          style="flex: 1;"
          use:enhance={() => {
            isLoading = true;
            return async ({ update }) => {
              isLoading = false;
              await update();
            };
          }}
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
        <form
          method="POST"
          action="?/bulkDeleteTreatments"
          style="flex: 1;"
          use:enhance={() => {
            isLoading = true;
            return async ({ update }) => {
              isLoading = false;
              await update();
            };
          }}
        >
          {#each treatmentsToDelete as treatment}
            <input type="hidden" name="treatmentIds" value={treatment._id} />
          {/each}
          <Button
            type="submit"
            variant="destructive"
            class="w-full"
            disabled={isLoading}
          >
            {isLoading
              ? "Deleting..."
              : `Delete ${treatmentsToDelete.length} Treatment${treatmentsToDelete.length !== 1 ? "s" : ""}`}
          </Button>
        </form>
      </Card.Footer>
    </Card.Root>
  </div>
{/if}
