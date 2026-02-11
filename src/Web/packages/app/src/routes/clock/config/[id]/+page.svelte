<script lang="ts">
  import { goto } from "$app/navigation";
  import { browser } from "$app/environment";
  import { page } from "$app/state";
  import { toast } from "svelte-sonner";
  import { X, Loader2 } from "lucide-svelte";
  import { StateHistory } from "runed";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getDefinitions } from "$lib/data/generated";
  import {
    update as updateClockFace,
    getByIdForEdit as getClockFaceById,
  } from "$lib/data/clockfaces.remote";
  import GlucoseChartCard from "$lib/components/dashboard/glucose-chart/GlucoseChartCard.svelte";
  import type { ClockElement, TrackerDefinitionDto } from "$lib/api/api-client";

  // Clock builder imports
  import {
    type ClockElementType,
    type InternalElement,
    type InternalRow,
    type InternalConfig,
    type DragState,
    initializeInternalConfig,
    toApiConfig,
    createInternalElement,
    createInternalRow,
    getBgColor,
    isTrackerBelowThreshold,
  } from "$lib/clock-builder";

  import {
    AddElementMenu,
    ClockBuilderHeader,
    ClockElementPreview,
    DisplaySettings,
    DropZone,
    ElementInspector,
  } from "$lib/components/clock-builder";

  // State
  let config = $state<InternalConfig>(initializeInternalConfig());
  let clockName = $state("My Clock Face");
  let saving = $state(false);
  let loading = $state(true);
  let selectedElementId = $state<string | null>(null);
  let addMenuOpen = $state<"top" | "bottom" | null>(null);
  let settingsOpen = $state(false);

  // Get ID from route params
  const clockFaceId = $derived(page.params.id);

  // Drag state
  let draggedElement = $state<DragState | null>(null);
  let dragOverRowIndex = $state<number | null>(null);

  // Undo/redo history - tracks config state changes
  const history = new StateHistory(
    () => JSON.stringify(config),
    (serialized) => {
      config = JSON.parse(serialized);
    }
  );

  // Realtime store for live preview
  const realtimeStore = getRealtimeStore();
  const currentBG = $derived(realtimeStore.currentBG);
  const bgDelta = $derived(realtimeStore.bgDelta);
  const direction = $derived(realtimeStore.direction);

  // Tracker definitions
  let trackerDefinitions = $state<TrackerDefinitionDto[]>([]);
  $effect(() => {
    if (browser) {
      getDefinitions({})
        .then((defs) => {
          trackerDefinitions = defs;
        })
        .catch(() => {
          trackerDefinitions = [];
        });
    }
  });

  // Time for preview
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  // Keyboard shortcuts for undo/redo
  function handleKeydown(e: KeyboardEvent) {
    if (
      e.target instanceof HTMLInputElement ||
      e.target instanceof HTMLTextAreaElement
    ) {
      return;
    }

    const isMac = navigator.platform.toUpperCase().indexOf("MAC") >= 0;
    const modifier = isMac ? e.metaKey : e.ctrlKey;

    if (modifier && e.key === "z" && !e.shiftKey) {
      e.preventDefault();
      if (history.canUndo) history.undo();
    } else if (
      (modifier && e.key === "z" && e.shiftKey) ||
      (modifier && e.key === "y")
    ) {
      e.preventDefault();
      if (history.canRedo) history.redo();
    }
  }

  // Computed styles
  const hasBackgroundImage = $derived(!!config.settings?.backgroundImage);
  const previewBgStyle = $derived(
    hasBackgroundImage
      ? `background-image: url(${config.settings.backgroundImage}); background-size: cover; background-position: center;`
      : config.settings?.bgColor
        ? `background-color: ${getBgColor(currentBG)};`
        : "background-color: #0a0a0a;"
  );
  const overlayOpacity = $derived(
    hasBackgroundImage
      ? (100 - (config.settings?.backgroundOpacity ?? 100)) / 100
      : 0
  );

  // Background chart element
  const backgroundChart = $derived.by(() => {
    for (const row of config.rows) {
      for (const element of row.elements) {
        if (element.type === "chart" && element.chartConfig?.asBackground) {
          return element;
        }
      }
    }
    return null;
  });

  // Selected element
  const selectedElement = $derived.by(() => {
    if (!selectedElementId) return null;
    for (let rowIndex = 0; rowIndex < config.rows.length; rowIndex++) {
      const element = config.rows[rowIndex].elements.find(
        (e) => e._id === selectedElementId
      );
      if (element) return { element, rowIndex };
    }
    return null;
  });

  // Element operations
  function addElement(type: ClockElementType, position: "top" | "bottom") {
    const internalElement = createInternalElement(type);
    if (config.rows.length === 0) {
      config.rows = [createInternalRow([internalElement])];
    } else if (position === "top") {
      config.rows[0].elements = [internalElement, ...config.rows[0].elements];
    } else {
      config.rows[config.rows.length - 1].elements = [
        ...config.rows[config.rows.length - 1].elements,
        internalElement,
      ];
    }
    selectedElementId = internalElement._id;
    addMenuOpen = null;
  }

  function removeElement(rowIndex: number, elementId: string) {
    config.rows[rowIndex].elements = config.rows[rowIndex].elements.filter(
      (e) => e._id !== elementId
    );
    if (config.rows[rowIndex].elements.length === 0 && config.rows.length > 1) {
      config.rows = config.rows.filter((_, i) => i !== rowIndex);
    }
    if (selectedElementId === elementId) selectedElementId = null;
  }

  function updateElement(
    rowIndex: number,
    elementId: string,
    updates: Partial<ClockElement>
  ) {
    config.rows[rowIndex].elements = config.rows[rowIndex].elements.map((el) =>
      el._id === elementId ? { ...el, ...updates } : el
    );
  }

  function updateStyle(
    rowIndex: number,
    elementId: string,
    styleUpdates: Record<string, unknown>
  ) {
    const row = config.rows[rowIndex];
    const element = row.elements.find((e) => e._id === elementId);
    if (!element) return;

    updateElement(rowIndex, elementId, {
      style: { ...element.style, ...styleUpdates },
    });
  }

  function updateCustomStyle(
    rowIndex: number,
    elementId: string,
    key: string,
    value: string
  ) {
    const row = config.rows[rowIndex];
    const element = row.elements.find((e) => e._id === elementId);
    if (!element) return;

    const custom = { ...(element.style?.custom || {}), [key]: value };
    updateStyle(rowIndex, elementId, { custom });
  }

  function removeCustomStyle(rowIndex: number, elementId: string, key: string) {
    const row = config.rows[rowIndex];
    const element = row.elements.find((e) => e._id === elementId);
    if (!element) return;

    const custom = { ...(element.style?.custom || {}) };
    delete custom[key];
    updateStyle(rowIndex, elementId, {
      custom: Object.keys(custom).length > 0 ? custom : undefined,
    });
  }

  // Drag and drop handlers
  function handleDragStart(
    e: DragEvent,
    rowIndex: number,
    elementIndex: number,
    element: InternalElement
  ) {
    draggedElement = { rowIndex, elementIndex, element };
    if (e.dataTransfer) {
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", element._id);
    }
  }

  function handleDragOver(e: DragEvent, rowIndex: number) {
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
    dragOverRowIndex = rowIndex;
  }

  function handleDragLeave() {
    dragOverRowIndex = null;
  }

  function handleDropNewRowTop(e: DragEvent) {
    e.preventDefault();
    if (!draggedElement) return;
    const {
      rowIndex: sourceRowIndex,
      elementIndex: sourceElementIndex,
      element,
    } = draggedElement;
    config.rows[sourceRowIndex].elements = config.rows[
      sourceRowIndex
    ].elements.filter((_, i) => i !== sourceElementIndex);
    const newRow = createInternalRow([element]);
    config.rows = [newRow, ...config.rows];
    const adjustedSourceIndex = sourceRowIndex + 1;
    if (
      config.rows[adjustedSourceIndex].elements.length === 0 &&
      config.rows.length > 1
    ) {
      config.rows = config.rows.filter((_, i) => i !== adjustedSourceIndex);
    }
    draggedElement = null;
    dragOverRowIndex = null;
  }

  function handleDropNewRowBottom(e: DragEvent) {
    e.preventDefault();
    if (!draggedElement) return;
    const {
      rowIndex: sourceRowIndex,
      elementIndex: sourceElementIndex,
      element,
    } = draggedElement;
    config.rows[sourceRowIndex].elements = config.rows[
      sourceRowIndex
    ].elements.filter((_, i) => i !== sourceElementIndex);
    const newRow = createInternalRow([element]);
    config.rows = [...config.rows, newRow];
    if (
      config.rows[sourceRowIndex].elements.length === 0 &&
      config.rows.length > 1
    ) {
      config.rows = config.rows.filter((_, i) => i !== sourceRowIndex);
    }
    draggedElement = null;
    dragOverRowIndex = null;
  }

  function handleDrop(e: DragEvent, targetRowIndex: number) {
    e.preventDefault();
    if (!draggedElement) return;
    const {
      rowIndex: sourceRowIndex,
      elementIndex: sourceElementIndex,
      element,
    } = draggedElement;
    config.rows[sourceRowIndex].elements = config.rows[
      sourceRowIndex
    ].elements.filter((_, i) => i !== sourceElementIndex);
    config.rows[targetRowIndex].elements = [
      ...config.rows[targetRowIndex].elements,
      element,
    ];
    if (
      config.rows[sourceRowIndex].elements.length === 0 &&
      config.rows.length > 1
    ) {
      config.rows = config.rows.filter((_, i) => i !== sourceRowIndex);
    }
    draggedElement = null;
    dragOverRowIndex = null;
  }

  function handleDragEnd() {
    draggedElement = null;
    dragOverRowIndex = null;
  }

  function resetToDefault() {
    config = initializeInternalConfig();
    clockName = "My Clock Face";
    selectedElementId = null;
    history.clear();
  }

  // Load clock face from route param on mount
  $effect(() => {
    if (!browser || !clockFaceId) return;
    loadClockFace(clockFaceId);
  });

  async function loadClockFace(id: string) {
    loading = true;
    try {
      const clockFace = await getClockFaceById(id);
      clockName = clockFace.name ?? "My Clock Face";
      if (clockFace.config) {
        config = initializeInternalConfig(clockFace.config);
      }
      history.clear();
    } catch (err) {
      console.error("Failed to load clock face:", err);
      toast.error("Failed to load clock face");
      goto("/clock");
    } finally {
      loading = false;
    }
  }

  function openClock() {
    goto(`/clock/${clockFaceId}`);
  }

  function copyLink() {
    navigator.clipboard.writeText(
      `${window.location.origin}/clock/${clockFaceId}`
    );
    toast.success("Link copied to clipboard");
  }

  async function saveConfiguration() {
    if (!browser || !clockFaceId) return;
    saving = true;
    try {
      await updateClockFace({
        id: clockFaceId,
        request: { name: clockName, config: toApiConfig(config) },
      });
      toast.success("Clock face saved");
    } catch (err) {
      console.error("Failed to save clock face:", err);
      toast.error("Failed to save clock face");
    } finally {
      saving = false;
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<svelte:head>
  <title>Clock Builder - Nocturne</title>
</svelte:head>

{#snippet chartElementPreview(element: InternalElement)}
  <GlucoseChartCard
    compact={true}
    heightClass="h-full"
    defaultFocusHours={element.hours || 3}
    initialShowIob={element.chartConfig?.showIob ?? false}
    initialShowCob={element.chartConfig?.showCob ?? false}
    initialShowBasal={element.chartConfig?.showBasal ?? false}
    initialShowBolus={element.chartConfig?.showBolus ?? true}
    initialShowCarbs={element.chartConfig?.showCarbs ?? true}
    initialShowDeviceEvents={element.chartConfig?.showDeviceEvents ?? false}
    initialShowAlarms={element.chartConfig?.showAlarms ?? false}
    initialShowScheduledTrackers={element.chartConfig?.showTrackers ?? false}
    showPredictions={element.chartConfig?.showPredictions ?? false}
  />
{/snippet}

{#snippet removeButton(rowIndex: number, elementId: string)}
  <button
    type="button"
    class="absolute -right-2 -top-2 z-10 flex size-5 items-center justify-center rounded-full bg-destructive text-destructive-foreground shadow-md transition-transform hover:scale-110"
    onclick={(e) => {
      e.stopPropagation();
      removeElement(rowIndex, elementId);
    }}
  >
    <X class="size-3" />
  </button>
{/snippet}

{#snippet draggableElement(
  element: InternalElement,
  rowIndex: number,
  elementIndex: number
)}
  {@const belowThreshold = isTrackerBelowThreshold(element)}
  <div class="relative">
    <button
      type="button"
      draggable="true"
      ondragstart={(e) => handleDragStart(e, rowIndex, elementIndex, element)}
      ondragend={handleDragEnd}
      class="relative cursor-grab rounded px-1 transition-all active:cursor-grabbing {selectedElementId ===
      element._id
        ? belowThreshold
          ? 'border-2 border-dashed border-primary/60'
          : 'ring-2 ring-primary ring-offset-2 ring-offset-black'
        : belowThreshold
          ? 'border border-dashed border-transparent hover:border-white/40'
          : 'hover:ring-1 hover:ring-white/30'}"
      onclick={() => (selectedElementId = element._id)}
    >
      {#if element.type === "chart"}
        <div
          class="overflow-hidden rounded"
          style="width: {element.width || 400}px; height: {element.height ||
            200}px;"
        >
          {@render chartElementPreview(element)}
        </div>
      {:else}
        <ClockElementPreview
          {element}
          {currentBG}
          {bgDelta}
          {direction}
          {currentTime}
          {trackerDefinitions}
        />
      {/if}
    </button>
    {#if selectedElementId === element._id}
      {@render removeButton(rowIndex, element._id)}
    {/if}
  </div>
{/snippet}

{#snippet elementRow(row: InternalRow, rowIndex: number)}
  <div
    class="group flex min-h-[40px] items-center gap-2 rounded-lg px-2 py-1 transition-colors {dragOverRowIndex ===
    rowIndex
      ? 'bg-primary/20 ring-2 ring-primary'
      : 'hover:bg-white/5'}"
    ondragover={(e) => handleDragOver(e, rowIndex)}
    ondragleave={handleDragLeave}
    ondrop={(e) => handleDrop(e, rowIndex)}
    role="list"
  >
    {#each row.elements as element, elementIndex (element._id)}
      {#if !(element.type === "chart" && element.chartConfig?.asBackground)}
        {@render draggableElement(element, rowIndex, elementIndex)}
      {/if}
    {/each}

    {#if row.elements.filter((e) => !(e.type === "chart" && e.chartConfig?.asBackground)).length === 0}
      <span class="text-sm text-white/30">Drop elements here</span>
    {/if}
  </div>
{/snippet}

{#if loading}
  <div class="flex min-h-dvh items-center justify-center bg-background">
    <Loader2 class="size-12 animate-spin text-muted-foreground" />
  </div>
{:else}
  <div class="flex min-h-dvh flex-col bg-background text-foreground">
    <!-- Header -->
    <ClockBuilderHeader
      {clockName}
      {saving}
      canUndo={history.canUndo}
      canRedo={history.canRedo}
      onNameChange={(name) => (clockName = name)}
      onUndo={() => history.undo()}
      onRedo={() => history.redo()}
      onCopyLink={copyLink}
      onSave={saveConfiguration}
      onPreview={openClock}
    >
      <DisplaySettings
        settings={config.settings}
        {hasBackgroundImage}
        open={settingsOpen}
        onOpenChange={(open) => (settingsOpen = open)}
        onSettingsChange={(settings) => (config.settings = settings)}
        onReset={resetToDefault}
      />
    </ClockBuilderHeader>

    <!-- Main content -->
    <div class="flex min-w-0 flex-1 overflow-hidden">
      <!-- Preview Canvas -->
      <div class="relative flex min-w-0 flex-1 flex-col overflow-hidden">
        <div
          class="absolute inset-4 flex flex-col items-center justify-center overflow-auto rounded-xl border-2 border-dashed border-muted-foreground/30"
          style={previewBgStyle}
        >
          <!-- Background overlay for image opacity -->
          {#if hasBackgroundImage}
            <div
              class="absolute inset-0 bg-black"
              style="opacity: {overlayOpacity}"
            ></div>
          {/if}

          <!-- Background chart -->
          {#if backgroundChart}
            {@const bgChartRowIndex = config.rows.findIndex((r) =>
              r.elements.some((e) => e._id === backgroundChart._id)
            )}
            <div class="absolute inset-0 z-0">
              {@render chartElementPreview(backgroundChart)}
            </div>
            <!-- Background chart selection button -->
            <div class="absolute left-2 top-2 z-20">
              <button
                type="button"
                class="rounded bg-black/50 px-2 py-1 text-xs text-white/70 transition-all hover:bg-black/70 hover:text-white {selectedElementId ===
                backgroundChart._id
                  ? 'ring-2 ring-primary'
                  : ''}"
                onclick={() => (selectedElementId = backgroundChart._id)}
              >
                Background Chart
              </button>
              {#if selectedElementId === backgroundChart._id}
                {@render removeButton(bgChartRowIndex, backgroundChart._id)}
              {/if}
            </div>
          {/if}

          <!-- Rows -->
          <div class="relative z-10 flex flex-col items-center gap-3 p-6">
            <!-- Top drop zone for new row (when dragging) or add menu -->
            {#if draggedElement}
              <DropZone
                position="top"
                isActive={dragOverRowIndex === -1}
                onDragOver={(e) => {
                  e.preventDefault();
                  dragOverRowIndex = -1;
                }}
                onDragLeave={handleDragLeave}
                onDrop={handleDropNewRowTop}
              />
            {:else}
              <AddElementMenu
                position="top"
                open={addMenuOpen === "top"}
                onOpenChange={(open) => (addMenuOpen = open ? "top" : null)}
                onAddElement={addElement}
              />
            {/if}

            {#each config.rows as row, rowIndex (row._id)}
              {@render elementRow(row, rowIndex)}
            {/each}

            <!-- Bottom drop zone for new row -->
            {#if draggedElement}
              <DropZone
                position="bottom"
                isActive={dragOverRowIndex === -2}
                onDragOver={(e) => {
                  e.preventDefault();
                  dragOverRowIndex = -2;
                }}
                onDragLeave={handleDragLeave}
                onDrop={handleDropNewRowBottom}
              />
            {:else}
              <AddElementMenu
                position="bottom"
                open={addMenuOpen === "bottom"}
                onOpenChange={(open) => (addMenuOpen = open ? "bottom" : null)}
                onAddElement={addElement}
              />
            {/if}
          </div>
        </div>
      </div>

      <!-- Element Inspector Sidebar -->
      {#if selectedElement}
        {@const { element, rowIndex } = selectedElement}
        <ElementInspector
          {element}
          {trackerDefinitions}
          onClose={() => (selectedElementId = null)}
          onRemove={() => removeElement(rowIndex, element._id)}
          onUpdateElement={(updates) =>
            updateElement(rowIndex, element._id, updates)}
          onUpdateStyle={(styleUpdates) =>
            updateStyle(rowIndex, element._id, styleUpdates)}
          onUpdateCustomStyle={(key, value) =>
            updateCustomStyle(rowIndex, element._id, key, value)}
          onRemoveCustomStyle={(key) =>
            removeCustomStyle(rowIndex, element._id, key)}
        />
      {/if}
    </div>
  </div>
{/if}
