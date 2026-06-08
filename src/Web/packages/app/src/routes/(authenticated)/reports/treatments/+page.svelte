<script lang="ts">
  import { page } from "$app/state";
  import { replaceState } from "$app/navigation";

  interface TreatmentSummary {
    totals?: {
      insulin?: { bolus?: number; basal?: number; scheduledBasal?: number; additionalBasal?: number };
      food?: { carbs?: number };
    };
    treatmentCount?: number;
  }
  import {
    TreatmentsDataTable,
    TreatmentEditDialog,
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
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    Calendar,
    X,
    Plus,
    Syringe,
    Utensils,
    Droplet,
    FileText,
    Smartphone,
  } from "lucide-svelte";
  import {
    formatInsulinDisplay,
    formatCarbDisplay,
    formatDateTimeCompact,
    bg,
    bgLabel,
  } from "$lib/utils/formatting";
  import { toast } from "svelte-sonner";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Import remote function forms and commands
  import {
    getTreatmentsData,
    deleteEntryForm,
    bulkDeleteEntries,
    updateEntry,
    createEntry,
  } from "./data.remote";
  import { toCreateEntryInput, toUpdateEntryInput } from "./entry-request";

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
      basalInjections: reportsResource.current?.basalInjections,
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

  // Edit dialog states
  let editDialogOpen = $state(false);
  let editRecord = $state<EntryRecord | null>(null);
  let editLoading = $state(false);

  // Reflect the open edit dialog in the URL (?edit=<kind>:<id>) so it can be
  // reloaded and deep-linked. The token is resolved against the loaded rows.
  const EDIT_PARAM = "edit";
  const editHistoryParam = {
    name: EDIT_PARAM,
    value: () =>
      editRecord?.data.id ? `${editRecord.kind}:${editRecord.data.id}` : "",
  };

  // On load (or deep link), open the dialog for the record named in ?edit=.
  // Runs once data is available; clears the param if the record isn't in range.
  let restoredFromUrl = false;
  $effect(() => {
    if (restoredFromUrl || !reportsResource.current) return;
    const token = page.url.searchParams.get(EDIT_PARAM);
    if (!token) {
      restoredFromUrl = true;
      return;
    }
    const sep = token.indexOf(":");
    const kind = sep === -1 ? token : token.slice(0, sep);
    const id = sep === -1 ? "" : token.slice(sep + 1);
    const found = allRows.find((r) => r.kind === kind && r.data.id === id);
    restoredFromUrl = true;
    if (found) {
      editRecord = found;
      editDialogOpen = true;
    } else {
      // Stale / out-of-range link: drop the param so the URL isn't misleading.
      const url = new URL(page.url);
      url.searchParams.delete(EDIT_PARAM);
      replaceState(url, page.state);
    }
  });

  const editCorrelatedRecords = $derived.by(() => {
    if (!editRecord?.data.correlationId) return [];
    return allRows.filter(
      (r) =>
        r.data.correlationId === editRecord!.data.correlationId &&
        r.data.id !== editRecord!.data.id,
    );
  });

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
          case "basalInjection":
            if (r.data.insulinContext?.insulinName)
              searchable.push(r.data.insulinContext.insulinName);
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
    const url = new URL(page.url);
    if (category === "all") {
      url.searchParams.delete("category");
    } else {
      url.searchParams.set("category", category);
    }
    // Use SvelteKit shallow routing so `page.url` stays authoritative (the edit
    // dialog reads it to keep its `?edit=` param in sync).
    replaceState(url, page.state);
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

  function handleRowClick(record: EntryRecord) {
    editRecord = record;
    editDialogOpen = true;
  }

  // Build an empty record of the chosen kind to open the dialog in "create"
  // mode. The dialog keys off the absent id to switch its title/buttons and the
  // save handler routes id-less records to createEntry.
  function makeBlankRecord(kind: EntryCategoryId): EntryRecord {
    const data = {
      mills: Date.now(),
      utcOffset: -new Date().getTimezoneOffset(),
    } as EntryRecord["data"];
    return { kind, data } as EntryRecord;
  }

  function handleAddTreatment(kind: EntryCategoryId) {
    editRecord = makeBlankRecord(kind);
    editDialogOpen = true;
  }

  const addKindIcons: Record<EntryCategoryId, typeof Plus> = {
    bolus: Syringe,
    carbs: Utensils,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
    basalInjection: Syringe,
  };

  function handleEditClose() {
    editDialogOpen = false;
    editRecord = null;
  }

  async function handleEditSave(record: EntryRecord) {
    editLoading = true;
    try {
      if (record.data.id) {
        await updateEntry(toUpdateEntryInput(record));
        toast.success("Record updated successfully");
      } else {
        await createEntry(toCreateEntryInput(record));
        toast.success("Record created successfully");
      }
      editDialogOpen = false;
      editRecord = null;
      reportsResource.refresh();
    } catch (error) {
      console.error("Save error:", error);
      toast.error(
        record.data.id ? "Failed to update record" : "Failed to create record",
      );
    } finally {
      editLoading = false;
    }
  }

  async function handleEditDelete(record: EntryRecord) {
    editDialogOpen = false;
    editRecord = null;
    confirmDelete(record);
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
          ? `${bg(record.data.mgdl)} ${bgLabel()}`
          : "BG Check";
      case "note":
        return record.data.text
          ? record.data.text.length > 50
            ? record.data.text.slice(0, 50) + "..."
            : record.data.text
          : "Note";
      case "deviceEvent":
        return record.data.eventType ?? "Device Event";
      case "basalInjection":
        return record.data.units
          ? `${record.data.units}U basal`
          : "Long-acting injection";
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
<div class="@container container mx-auto space-y-6 p-3 @md:p-6">
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
    <Card.Content class="@container p-4">
      <div
        class="flex flex-col gap-4 @lg:flex-row @lg:items-end @lg:justify-between"
      >
        <div class="flex flex-1 flex-col gap-4 @lg:flex-row @lg:items-end">
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
          <DropdownMenu.Root>
            <DropdownMenu.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <Button {...props} size="sm">
                  <Plus class="mr-1 h-4 w-4" />
                  Add Treatment
                </Button>
              {/snippet}
            </DropdownMenu.Trigger>
            <DropdownMenu.Content align="end">
              {#each Object.entries(ENTRY_CATEGORIES) as [id, cat]}
                {@const Icon = addKindIcons[id as EntryCategoryId]}
                <DropdownMenu.Item
                  onclick={() => handleAddTreatment(id as EntryCategoryId)}
                >
                  <Icon class="mr-2 h-4 w-4 {cat.colorClass}" />
                  {cat.name}
                </DropdownMenu.Item>
              {/each}
            </DropdownMenu.Content>
          </DropdownMenu.Root>
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
        onRowClick={handleRowClick}
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

<!-- Edit Record Dialog -->
<TreatmentEditDialog
  bind:open={editDialogOpen}
  record={editRecord}
  correlatedRecords={editCorrelatedRecords}
  isLoading={editLoading}
  historyParam={editHistoryParam}
  onClose={handleEditClose}
  onSave={handleEditSave}
  onDelete={editRecord?.data.id ? handleEditDelete : undefined}
/>
{/if}
