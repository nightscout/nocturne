<script lang="ts">
  import {
    Card,
    CardContent,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { Textarea } from "$lib/components/ui/textarea";
  import {
    TrackerCompletionDialog,
    TrackerStartDialog,
    type TrackerNotification,
  } from "$lib/components/trackers";
  import ActiveTrackersTab from "$lib/components/trackers/ActiveTrackersTab.svelte";
  import TrackerHistoryTab from "$lib/components/trackers/TrackerHistoryTab.svelte";
  import TrackerDefinitionsTab from "$lib/components/trackers/TrackerDefinitionsTab.svelte";
  import TrackerPresetsTab from "$lib/components/trackers/TrackerPresetsTab.svelte";
  import TrackerEditorDialog from "$lib/components/trackers/TrackerEditorDialog.svelte";
  import {
    Timer,
    AlertTriangle,
    History,
    Settings2,
    Bookmark,
    Loader2,
    Activity,
  } from "lucide-svelte";
  import { tick, onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import * as trackersRemote from "$api/generated/trackers.generated.remote";
  import {
    NotificationUrgency,
    TrackerCategory,
    CompletionReason,
    DashboardVisibility,
    TrackerVisibility,
    TrackerMode,
    type TrackerDefinitionDto,
    type TrackerInstanceDto,
    type TrackerPresetDto,
  } from "$api";

  // Auth state
  const authStore = getAuthStore();
  const isAuthenticated = $derived(authStore.isAuthenticated);

  // State
  let activeTab = $state("active");
  let loading = $state(true);
  let error = $state<string | null>(null);

  let definitions = $state<TrackerDefinitionDto[]>([]);
  let activeInstances = $state<TrackerInstanceDto[]>([]);
  let historyInstances = $state<TrackerInstanceDto[]>([]);
  let presets = $state<TrackerPresetDto[]>([]);

  // Dialog state
  let isDefinitionDialogOpen = $state(false);
  let editingDefinition = $state<TrackerDefinitionDto | null>(null);
  let isNewDefinition = $state(false);

  // Delete confirmation dialog state
  let isDeleteDefinitionDialogOpen = $state(false);
  let deletingDefinitionId = $state<string | null>(null);
  let isDeleteInstanceDialogOpen = $state(false);
  let deletingInstanceId = $state<string | null>(null);
  let isDeletePresetDialogOpen = $state(false);
  let deletingPresetId = $state<string | null>(null);

  // Preset dialog state
  let isPresetDialogOpen = $state(false);
  let isNewPreset = $state(false);
  let formPresetName = $state("");
  let formPresetDefinitionId = $state<string | undefined>(undefined);
  let formPresetDefaultStartNotes = $state("");

  // Form references for form()-based remote functions

  // Form state for definition
  let formName = $state("");
  let formDescription = $state("");
  let formCategory = $state<TrackerCategory>(TrackerCategory.Consumable);
  let formIcon = $state("activity");
  let formLifespanHours = $state<number | undefined>(undefined);
  let formNotifications = $state<TrackerNotification[]>([]);
  let formIsFavorite = $state(false);
  let formDashboardVisibility = $state<DashboardVisibility>(
    DashboardVisibility.Always
  );
  let formVisibility = $state<TrackerVisibility>(TrackerVisibility.Public);
  let formMode = $state<TrackerMode>(TrackerMode.Duration);
  let formStartEventType = $state<string | undefined>(undefined);
  let formCompletionEventType = $state<string | undefined>(undefined);

  // Helper to convert API format to notifications array
  function definitionToNotifications(
    def: TrackerDefinitionDto
  ): TrackerNotification[] {
    // Use notificationThresholds array from API
    if (def.notificationThresholds && def.notificationThresholds.length > 0) {
      return def.notificationThresholds.map((t, i) => ({
        id: t.id,
        urgency: t.urgency ?? NotificationUrgency.Info,
        hours: t.hours,
        description: t.description ?? "",
        displayOrder: t.displayOrder ?? i,
      }));
    }

    return [];
  }

  // Start instance dialog
  let isStartDialogOpen = $state(false);
  let startDefinition = $state<TrackerDefinitionDto | null>(null);

  function openStartDialog(definition: TrackerDefinitionDto) {
    if (!requireAuth()) return;

    startDefinition = definition;
    isStartDialogOpen = true;
  }

  // Complete instance dialog
  let isCompleteDialogOpen = $state(false);
  let completingInstance = $state<TrackerInstanceDto | null>(null);
  let completingDefinition = $state<TrackerDefinitionDto | null>(null);

  function openCompleteDialog(instanceId: string) {
    if (!requireAuth()) return;

    const instance = activeInstances.find((i) => i.id === instanceId);
    if (!instance) return;

    completingInstance = instance;
    completingDefinition =
      definitions.find((d) => d.id === instance.definitionId) || null;
    isCompleteDialogOpen = true;
  }

  // Derived counts
  const activeCount = $derived(activeInstances.length);

  // Category labels
  const categoryLabels: Record<TrackerCategory, string> = {
    [TrackerCategory.Consumable]: "Consumable",
    [TrackerCategory.Reservoir]: "Reservoir",
    [TrackerCategory.Appointment]: "Appointment",
    [TrackerCategory.Reminder]: "Reminder",
    [TrackerCategory.Custom]: "Custom",
    [TrackerCategory.Sensor]: "Sensor",
    [TrackerCategory.Cannula]: "Cannula",
    [TrackerCategory.Battery]: "Battery",
  };

  // Completion reason labels
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell Off",
    [CompletionReason.ReplacedEarly]: "Replaced Early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Cancelled",
    [CompletionReason.Missed]: "Missed",
  };

  // Category colors (icons handled by TrackerCategoryIcon component)
  const categoryColors: Record<TrackerCategory, string> = {
    [TrackerCategory.Consumable]: "text-blue-500",
    [TrackerCategory.Reservoir]: "text-purple-500",
    [TrackerCategory.Appointment]: "text-green-500",
    [TrackerCategory.Reminder]: "text-orange-500",
    [TrackerCategory.Custom]: "text-gray-500",
    [TrackerCategory.Sensor]: "text-cyan-500",
    [TrackerCategory.Cannula]: "text-pink-500",
    [TrackerCategory.Battery]: "text-yellow-500",
  };

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [defs, active, history, prsts] = await Promise.all([
        trackersRemote.getDefinitions(undefined),
        trackersRemote.getActiveInstances(),
        trackersRemote.getInstanceHistory(undefined),
        trackersRemote.getPresets(),
      ]);
      definitions = defs || [];
      activeInstances = active || [];
      historyInstances = history || [];
      presets = prsts || [];
    } catch (err) {
      console.error("Failed to load tracker data:", err);
      error = "Failed to load tracker data";
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    loadData();
  });

  // Format age
  function formatAge(hours: number): string {
    if (hours < 1) return `${Math.floor(hours * 60)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    const days = Math.floor(hours / 24);
    const h = Math.floor(hours % 24);
    return h > 0 ? `${days}d ${h}h` : `${days}d`;
  }

  // Format date
  function formatDate(dateStr: any): string {
    if (!dateStr) return "";
    return new Date(dateStr).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Get time remaining for instance
  function getTimeRemaining(instance: TrackerInstanceDto): number | undefined {
    const def = definitions.find((d) => d.id === instance.definitionId);
    if (!def || !def.lifespanHours || instance.ageHours === undefined)
      return undefined;
    return def.lifespanHours - instance.ageHours;
  }

  // Get notification level for instance
  function getInstanceLevel(
    instance: TrackerInstanceDto
  ): NotificationUrgency | null {
    const def = definitions.find((d) => d.id === instance.definitionId);
    if (!def || !instance.ageHours || !def.notificationThresholds) return null;

    // Find the highest urgency threshold that the age exceeds
    let highestUrgency: NotificationUrgency | null = null;
    let highestLevel = -1;

    const urgencyOrder: Record<NotificationUrgency, number> = {
      [NotificationUrgency.Info]: 0,
      [NotificationUrgency.Warn]: 1,
      [NotificationUrgency.Hazard]: 2,
      [NotificationUrgency.Urgent]: 3,
    };

    for (const threshold of def.notificationThresholds) {
      if (threshold.hours && instance.ageHours >= threshold.hours) {
        const level =
          urgencyOrder[threshold.urgency ?? NotificationUrgency.Info];
        if (level > highestLevel) {
          highestLevel = level;
          highestUrgency = threshold.urgency ?? NotificationUrgency.Info;
        }
      }
    }

    return highestUrgency;
  }

  // Level styling
  function getLevelStyle(level: NotificationUrgency | null): string {
    switch (level) {
      case NotificationUrgency.Urgent:
        return "border-red-500 bg-red-500/10";
      case NotificationUrgency.Hazard:
        return "border-orange-500 bg-orange-500/10";
      case NotificationUrgency.Warn:
        return "border-yellow-500 bg-yellow-500/10";
      case NotificationUrgency.Info:
        return "border-blue-500 bg-blue-500/10";
      default:
        return "";
    }
  }

  // Redirect to login if not authenticated
  function requireAuth(): boolean {
    if (!isAuthenticated) {
      const returnUrl = encodeURIComponent(window.location.pathname);
      goto(`/auth/login?returnUrl=${returnUrl}`);
      return false;
    }
    return true;
  }

  // Open definition dialog
  function openNewDefinition() {
    if (!requireAuth()) return;

    isNewDefinition = true;
    editingDefinition = null;
    formName = "";
    formDescription = "";
    formCategory = TrackerCategory.Consumable;
    formIcon = "activity";
    formLifespanHours = undefined;
    formNotifications = [];
    formIsFavorite = false;
    formDashboardVisibility = DashboardVisibility.Always;
    formVisibility = TrackerVisibility.Public;
    formMode = TrackerMode.Duration;
    formStartEventType = undefined;
    formCompletionEventType = undefined;
    isDefinitionDialogOpen = true;
  }

  function openEditDefinition(def: TrackerDefinitionDto) {
    if (!requireAuth()) return;

    isNewDefinition = false;
    editingDefinition = def;
    formName = def.name || "";
    formDescription = def.description || "";
    formCategory = def.category ?? TrackerCategory.Consumable;
    formIcon = def.icon || "activity";
    formLifespanHours = def.lifespanHours;
    formNotifications = definitionToNotifications(def);
    formIsFavorite = def.isFavorite ?? false;
    formDashboardVisibility =
      def.dashboardVisibility ?? DashboardVisibility.Always;
    formVisibility = def.visibility ?? TrackerVisibility.Public;
    formMode = def.mode ?? TrackerMode.Duration;
    formStartEventType = def.startEventType ?? undefined;
    formCompletionEventType = def.completionEventType ?? undefined;
    isDefinitionDialogOpen = true;
  }

  // Delete definition
  function openDeleteDefinitionDialog(id: string) {
    if (!requireAuth()) return;

    deletingDefinitionId = id;
    isDeleteDefinitionDialogOpen = true;
  }

  async function confirmDeleteDefinition() {
    if (!deletingDefinitionId) return;
    try {
      await trackersRemote.deleteDefinition(deletingDefinitionId);
      await loadData();
      await tick();
      isDeleteDefinitionDialogOpen = false;
      deletingDefinitionId = null;
    } catch (err) {
      console.error("Failed to delete definition:", err);
    }
  }

  // Start instance

  // Delete instance
  function openDeleteInstanceDialog(id: string) {
    if (!requireAuth()) return;

    deletingInstanceId = id;
    isDeleteInstanceDialogOpen = true;
  }

  async function confirmDeleteInstance() {
    if (!deletingInstanceId) return;
    try {
      await trackersRemote.deleteInstance(deletingInstanceId);
      await loadData();
      await tick();
      isDeleteInstanceDialogOpen = false;
      deletingInstanceId = null;
    } catch (err) {
      console.error("Failed to delete instance:", err);
    }
  }

  // Apply preset
  async function applyPresetHandler(presetId: string) {
    if (!requireAuth()) return;

    try {
      await trackersRemote.applyPreset({ id: presetId });
      await loadData();
      await tick();
    } catch (err) {
      console.error("Failed to apply preset:", err);
    }
  }

  // Create preset
  function openNewPreset() {
    if (!requireAuth()) return;

    isNewPreset = true;
    formPresetName = "";
    formPresetDefinitionId = definitions[0]?.id ?? undefined;
    formPresetDefaultStartNotes = "";
    isPresetDialogOpen = true;
  }

  async function savePreset() {
    if (!formPresetName || !formPresetDefinitionId) return;
    try {
      // API only supports create, not update - so always create
      await trackersRemote.createPreset({
        name: formPresetName,
        definitionId: formPresetDefinitionId,
        defaultStartNotes: formPresetDefaultStartNotes || undefined,
      });
      await loadData();
      await tick();
      isPresetDialogOpen = false;
    } catch (err) {
      console.error("Failed to save preset:", err);
    }
  }

  // Delete preset
  function openDeletePresetDialog(id: string) {
    if (!requireAuth()) return;

    deletingPresetId = id;
    isDeletePresetDialogOpen = true;
  }

  async function confirmDeletePreset() {
    if (!deletingPresetId) return;
    try {
      await trackersRemote.deletePreset(deletingPresetId);
      await loadData();
      await tick();
      isDeletePresetDialogOpen = false;
      deletingPresetId = null;
    } catch (err) {
      console.error("Failed to delete preset:", err);
    }
  }
</script>

<svelte:head>
  <title>Notifications & Trackers - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <Timer class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">
        Notifications & Trackers
      </h1>
      <p class="text-muted-foreground">
        Track consumables, appointments, and reminders
      </p>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List class="grid w-full grid-cols-4">
        <Tabs.Trigger value="active" class="gap-2">
          <Activity class="h-4 w-4" />
          Active
          {#if activeCount > 0}
            <Badge variant="secondary" class="ml-1">{activeCount}</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="history" class="gap-2">
          <History class="h-4 w-4" />
          History
        </Tabs.Trigger>
        <Tabs.Trigger value="definitions" class="gap-2">
          <Settings2 class="h-4 w-4" />
          Definitions
        </Tabs.Trigger>
        <Tabs.Trigger value="presets" class="gap-2">
          <Bookmark class="h-4 w-4" />
          Presets
        </Tabs.Trigger>
      </Tabs.List>

      <!-- Active Instances Tab -->
            <ActiveTrackersTab
        {definitions}
        {activeInstances}
        {openStartDialog}
        {openCompleteDialog}
        {openDeleteInstanceDialog}
        {getInstanceLevel}
        {getTimeRemaining}
        {getLevelStyle}
        {formatAge}
        {formatDate}
      />

      <!-- History Tab -->
            <TrackerHistoryTab
        {historyInstances}
        {completionReasonLabels}
        {formatAge}
        {formatDate}
      />

      <!-- Definitions Tab -->
            <TrackerDefinitionsTab
        {definitions}
        {categoryLabels}
        {categoryColors}
        {openNewDefinition}
        {openStartDialog}
        {openEditDefinition}
        {openDeleteDefinitionDialog}
      />

      <!-- Presets Tab -->
            <TrackerPresetsTab
        {definitions}
        {presets}
        {openNewPreset}
        {applyPresetHandler}
        {openDeletePresetDialog}
      />
    </Tabs.Root>
  {/if}
</div>



<!-- Definition Dialog -->
<!-- Tracker Editor Dialog -->
<TrackerEditorDialog
  bind:open={isDefinitionDialogOpen}
  {isNewDefinition}
  {editingDefinition}
  bind:formName
  bind:formDescription
  bind:formCategory
  bind:formIcon
  bind:formLifespanHours
  bind:formNotifications
  bind:formIsFavorite
  bind:formDashboardVisibility
  bind:formVisibility
  bind:formMode
  bind:formStartEventType
  bind:formCompletionEventType
  {categoryLabels}
  {loadData}
/>

<!-- Start Instance Dialog -->
<TrackerStartDialog
  bind:open={isStartDialogOpen}
  definition={startDefinition}
  history={historyInstances}
  onClose={() => (startDefinition = null)}
  onStart={() => {
    startDefinition = null;
    loadData();
  }}
/>

<!-- Complete Instance Dialog -->
<TrackerCompletionDialog
  bind:open={isCompleteDialogOpen}
  instanceId={completingInstance?.id ?? null}
  instanceName={completingInstance?.definitionName ?? "tracker"}
  category={completingDefinition?.category}
  definitionId={completingInstance?.definitionId}
  completionEventType={completingDefinition?.completionEventType}
  onClose={() => {
    completingInstance = null;
    completingDefinition = null;
  }}
  onComplete={() => {
    completingInstance = null;
    completingDefinition = null;
    loadData();
  }}
/>

<!-- Delete Definition Confirmation Dialog -->
<AlertDialog.Root bind:open={isDeleteDefinitionDialogOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete Tracker Definition</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete this tracker definition? This action
        cannot be undone. Any active instances using this definition will
        remain, but you won't be able to start new ones.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel
        onclick={() => {
          isDeleteDefinitionDialogOpen = false;
          deletingDefinitionId = null;
        }}
      >
        Cancel
      </AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={confirmDeleteDefinition}
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
      >
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- Delete Instance Confirmation Dialog -->
<AlertDialog.Root bind:open={isDeleteInstanceDialogOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete Tracker Instance</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete this tracker instance? This action
        cannot be undone.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel
        onclick={() => {
          isDeleteInstanceDialogOpen = false;
          deletingInstanceId = null;
        }}
      >
        Cancel
      </AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={confirmDeleteInstance}
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
      >
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<!-- Preset Dialog -->
<Dialog.Root bind:open={isPresetDialogOpen}>
  <Dialog.Content class="sm:max-w-[425px]">
    <Dialog.Header>
      <Dialog.Title>
        {isNewPreset ? "New Preset" : "Edit Preset"}
      </Dialog.Title>
      <Dialog.Description>
        Create a quick preset for one-click tracker activation.
      </Dialog.Description>
    </Dialog.Header>
    <div class="grid gap-4 py-4">
      <div class="space-y-2">
        <Label for="presetName">Preset Name</Label>
        <Input
          id="presetName"
          bind:value={formPresetName}
          placeholder="e.g., G7 Sensor (Left Arm)"
        />
      </div>
      <div class="space-y-2">
        <Label for="presetDefinition">Tracker Definition</Label>
        <Select.Root type="single" bind:value={formPresetDefinitionId}>
          <Select.Trigger>
            {definitions.find((d) => d.id === formPresetDefinitionId)?.name ??
              "Select a definition"}
          </Select.Trigger>
          <Select.Content>
            {#each definitions as def}
              <Select.Item value={def.id ?? ""} label={def.name ?? ""} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="presetNotes">Default Start Notes (optional)</Label>
        <Textarea
          id="presetNotes"
          bind:value={formPresetDefaultStartNotes}
          placeholder="e.g., Left arm, upper"
        />
        <p class="text-xs text-muted-foreground">
          These notes will be pre-filled when applying this preset.
        </p>
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isPresetDialogOpen = false)}>
        Cancel
      </Button>
      <Button
        onclick={savePreset}
        disabled={!formPresetName || !formPresetDefinitionId}
      >
        {isNewPreset ? "Create" : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Preset Confirmation Dialog -->
<AlertDialog.Root bind:open={isDeletePresetDialogOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Delete Preset</AlertDialog.Title>
      <AlertDialog.Description>
        Are you sure you want to delete this preset? This action cannot be
        undone.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel
        onclick={() => {
          isDeletePresetDialogOpen = false;
          deletingPresetId = null;
        }}
      >
        Cancel
      </AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={confirmDeletePreset}
        class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
      >
        Delete
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
