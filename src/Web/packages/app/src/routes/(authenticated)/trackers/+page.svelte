<script lang="ts">
  import { formatAge } from "#lib/components/trackers/age.svelte.js";
  import { formatDayTime } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import {
    TrackerCompletionDialog,
    TrackerStartDialog,
    ReservoirReportDialog,
    type TrackerNotification,
  } from "#lib/components/trackers/index.js";
  import { reachedUrgency } from "#lib/components/trackers/schedule.js";
  import ActiveTrackersTab from "#lib/components/trackers/ActiveTrackersTab.svelte";
  import TrackerHistoryTab from "#lib/components/trackers/TrackerHistoryTab.svelte";
  import TrackerDefinitionsTab from "#lib/components/trackers/TrackerDefinitionsTab.svelte";
  import TrackerPresetsTab from "#lib/components/trackers/TrackerPresetsTab.svelte";
  import TrackerEditorDialog from "#lib/components/trackers/TrackerEditorDialog.svelte";
  import Timer from "@lucide/svelte/icons/timer";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import History from "@lucide/svelte/icons/history";
  import Settings2 from "@lucide/svelte/icons/settings-2";
  import Bookmark from "@lucide/svelte/icons/bookmark";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Activity from "@lucide/svelte/icons/activity";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import * as trackersRemote from "#lib/api/generated/trackers.generated.remote.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { toast } from "svelte-sonner";
  import { coachmark } from "@nocturne/coach";
  import { coachFor } from "#lib/coach-marks/copy.svelte.js";
  import {
    NotificationUrgency,
    TrackerCategory,
    CompletionReason,
    DashboardVisibility,
    TrackerMode,
    ReservoirReportKind,
    PumpCapability,
    type TrackerDefinitionDto,
    type TrackerInstanceDto,
    type TrackerPresetDto,
  } from "#lib/api/index.js";

  // Auth state from the server-resolved session. This route sits behind the
  // authenticated layout and its own server guard, so page.data.user is populated for
  // every visitor. The client auth store is unauthenticated until its async
  // session load resolves, so gating actions on it bounces fast clicks to login.
  const isAuthenticated = $derived(!!page.data.user);

  // State
  let activeTab = $state("active");

  const definitionsQuery = trackersRemote.getDefinitions(undefined);
  const activeInstancesQuery = trackersRemote.getActiveInstances();
  const historyInstancesQuery = trackersRemote.getInstanceHistory(undefined);
  const presetsQuery = trackersRemote.getPresets();

  // Mirrors via `.current` for event handlers (which run outside the boundary's
  // template scope and can't see {@const} bindings).
  const definitions = $derived<TrackerDefinitionDto[]>(definitionsQuery.current ?? []);
  const activeInstances = $derived<TrackerInstanceDto[]>(activeInstancesQuery.current ?? []);
  const historyInstances = $derived<TrackerInstanceDto[]>(historyInstancesQuery.current ?? []);
  const presets = $derived<TrackerPresetDto[]>(presetsQuery.current ?? []);

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
  let formLowReservoirUnits = $state<number | undefined>(undefined);
  let formLowReservoirUrgency = $state<NotificationUrgency>(
    NotificationUrgency.Warn
  );
  let formNotifications = $state<TrackerNotification[]>([]);
  let formIsFavorite = $state(false);
  let formDashboardVisibility = $state<DashboardVisibility>(
    DashboardVisibility.Always
  );
  let formMode = $state<TrackerMode>(TrackerMode.Duration);
  let formStartEventType = $state<string | undefined>(undefined);
  let formCompletionEventType = $state<string | undefined>(undefined);
  let formTriggerEventTypes = $state<string[]>([]);
  let formEndEventTypes = $state<string[]>([]);
  let formCovers = $state<PumpCapability[]>([]);
  let formTriggerNotesContains = $state("");

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
        alertRuleId: t.alertRuleId ?? undefined,
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

  // Reservoir report dialog
  let isReservoirReportDialogOpen = $state(false);

  function openReservoirReportDialog() {
    if (!requireAuth()) return;
    isReservoirReportDialogOpen = true;
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
    [TrackerCategory.Pod]: "Pod",
  };

  // Completion reason labels
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell off",
    [CompletionReason.ReplacedEarly]: "Replaced early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Canceled",
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
    [TrackerCategory.Pod]: "text-rose-500",
  };

  // Format date
  function formatDate(dateStr: Date | string | undefined): string {
    if (!dateStr) return "";
    return formatDayTime(dateStr);
  }

  // Get time remaining for instance
  function getTimeRemaining(instance: TrackerInstanceDto): number | undefined {
    if (!instance.expectedEndAt) return undefined;
    return (Date.parse(instance.expectedEndAt) - Date.now()) / (60 * 60 * 1000);
  }

  function getInstanceLevel(
    instance: TrackerInstanceDto
  ): NotificationUrgency | null {
    return reachedUrgency(instance, Date.now());
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
      goto(resolve(`auth/login?returnUrl=${returnUrl}`));
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
    formLowReservoirUnits = undefined;
    formLowReservoirUrgency = NotificationUrgency.Warn;
    formNotifications = [];
    formIsFavorite = false;
    formDashboardVisibility = DashboardVisibility.Always;
    formMode = TrackerMode.Duration;
    formStartEventType = undefined;
    formCompletionEventType = undefined;
    formTriggerEventTypes = [];
    formEndEventTypes = [];
    formCovers = [];
    formTriggerNotesContains = "";
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
    formLowReservoirUnits = def.lowReservoirUnits ?? undefined;
    formLowReservoirUrgency =
      def.lowReservoirUrgency ?? NotificationUrgency.Warn;
    formNotifications = definitionToNotifications(def);
    formIsFavorite = def.isFavorite ?? false;
    formDashboardVisibility =
      def.dashboardVisibility ?? DashboardVisibility.Always;
    formMode = def.mode ?? TrackerMode.Duration;
    formStartEventType = def.startEventType ?? undefined;
    formCompletionEventType = def.completionEventType ?? undefined;
    formTriggerEventTypes = [...def.triggerEventTypes ?? []];
    formEndEventTypes = [...def.endEventTypes ?? []];
    formCovers = [...def.covers ?? []];
    formTriggerNotesContains = def.triggerNotesContains ?? "";
    isDefinitionDialogOpen = true;
  }

  // Delete definition
  function openDeleteDefinitionDialog(id: string) {
    if (!requireAuth()) return;

    deletingDefinitionId = id;
    isDeleteDefinitionDialogOpen = true;
  }

  // Its instances and presets go with it on the server.
  async function confirmDeleteDefinition() {
    const id = deletingDefinitionId;
    if (!id) return;
    isDeleteDefinitionDialogOpen = false;
    deletingDefinitionId = null;
    const notOfDefinition = <T extends { definitionId?: string }>(current: T[]) =>
      current.filter((x) => x.definitionId !== id);
    try {
      await trackersRemote.deleteDefinition(id).updates(
        definitionsQuery.withOverride((current) => current.filter((d) => d.id !== id)),
        activeInstancesQuery.withOverride(notOfDefinition),
        historyInstancesQuery.withOverride(notOfDefinition),
        presetsQuery.withOverride(notOfDefinition)
      );
    } catch (err) {
      toast.error(describeSubmitError(err));
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
    const id = deletingInstanceId;
    if (!id) return;
    isDeleteInstanceDialogOpen = false;
    deletingInstanceId = null;
    try {
      await trackersRemote.deleteInstance(id).updates(
        activeInstancesQuery.withOverride((current) => current.filter((i) => i.id !== id))
      );
    } catch (err) {
      toast.error(describeSubmitError(err));
    }
  }

  // Apply preset
  async function applyPresetHandler(presetId: string) {
    if (!requireAuth()) return;

    try {
      await trackersRemote.applyPreset({ id: presetId })
        .updates(activeInstancesQuery, historyInstancesQuery);
    } catch (err) {
      toast.error(describeSubmitError(err));
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
      }).updates(presetsQuery);
      isPresetDialogOpen = false;
    } catch (err) {
      toast.error(describeSubmitError(err));
    }
  }

  // Delete preset
  function openDeletePresetDialog(id: string) {
    if (!requireAuth()) return;

    deletingPresetId = id;
    isDeletePresetDialogOpen = true;
  }

  async function confirmDeletePreset() {
    const id = deletingPresetId;
    if (!id) return;
    isDeletePresetDialogOpen = false;
    deletingPresetId = null;
    try {
      await trackersRemote.deletePreset(id).updates(
        presetsQuery.withOverride((current) => current.filter((p) => p.id !== id))
      );
    } catch (err) {
      toast.error(describeSubmitError(err));
    }
  }
</script>

<svelte:head>
  <title>Trackers - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <Timer class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Trackers</h1>
      <p class="text-muted-foreground">
        Track consumables, appointments, and reminders
      </p>
    </div>
  </div>

  <svelte:boundary>
    {#snippet pending()}
      <div class="flex items-center justify-center py-12">
        <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    {/snippet}
    {#snippet failed(error, reset)}
      <Card variant="destructive">
        <CardContent class="py-6 text-center">
          <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
          <p class="text-destructive">
            {remoteErrorMessage(error, "Failed to load tracker data")}
          </p>
          <Button variant="outline" class="mt-4" onclick={reset}>Retry</Button>
        </CardContent>
      </Card>
    {/snippet}

    {void await Promise.all([
      definitionsQuery,
      activeInstancesQuery,
      historyInstancesQuery,
      presetsQuery
    ])} 

    <Tabs.Root bind:value={activeTab} class="space-y-6">
      <Tabs.List variant="wrap">
        <Tabs.Trigger value="active" class="min-h-11 @xl:min-h-0">
          <Activity class="h-4 w-4" />
          Active
          {#if activeCount > 0}
            <Badge variant="secondary" class="ml-1">{activeCount}</Badge>
          {/if}
        </Tabs.Trigger>
        <Tabs.Trigger value="history" class="min-h-11 @xl:min-h-0">
          <History class="h-4 w-4" />
          History
        </Tabs.Trigger>
        <div class="flex" {@attach coachmark({ ...coachFor("tracker-intro.settings") })}>
          <Tabs.Trigger value="definitions" class="min-h-11 @xl:min-h-0">
            <Settings2 class="h-4 w-4" />
            Definitions
          </Tabs.Trigger>
        </div>
        <Tabs.Trigger value="presets" class="min-h-11 @xl:min-h-0">
          <Bookmark class="h-4 w-4" />
          Presets
        </Tabs.Trigger>
      </Tabs.List>

      <!-- Active Instances Tab -->
      <ActiveTrackersTab
        definitions={definitions}
        activeInstances={activeInstances}
        openStartDialog={openStartDialog}
        openCompleteDialog={openCompleteDialog}
        openReservoirReportDialog={openReservoirReportDialog}
        openDeleteInstanceDialog={openDeleteInstanceDialog}
        getInstanceLevel={getInstanceLevel}
        getTimeRemaining={getTimeRemaining}
        getLevelStyle={getLevelStyle}
        formatAge={formatAge}
        formatDate={formatDate}
      />

      <!-- History Tab -->
      <TrackerHistoryTab
        historyInstances={historyInstances}
        completionReasonLabels={completionReasonLabels}
        formatAge={formatAge}
        formatDate={formatDate}
      />

      <!-- Definitions Tab -->
      <TrackerDefinitionsTab
        definitions={definitions}
        categoryLabels={categoryLabels}
        categoryColors={categoryColors}
        openNewDefinition={openNewDefinition}
        openStartDialog={openStartDialog}
        openEditDefinition={openEditDefinition}
        openDeleteDefinitionDialog={openDeleteDefinitionDialog}
      />

      <!-- Presets Tab -->
      <TrackerPresetsTab
        definitions={definitions}
        presets={presets}
        openNewPreset={openNewPreset}
        applyPresetHandler={applyPresetHandler}
        openDeletePresetDialog={openDeletePresetDialog}
      />
    </Tabs.Root>
  </svelte:boundary>
</div>



<!-- Definition Dialog -->
<!-- Tracker Editor Dialog -->
<TrackerEditorDialog
  bind:open={isDefinitionDialogOpen}
  isNewDefinition={isNewDefinition}
  editingDefinition={editingDefinition}
  bind:formName
  bind:formDescription
  bind:formCategory
  bind:formIcon
  bind:formLifespanHours
  bind:formLowReservoirUnits
  bind:formLowReservoirUrgency
  bind:formNotifications
  bind:formIsFavorite
  bind:formDashboardVisibility
  bind:formMode
  bind:formStartEventType
  bind:formCompletionEventType
  bind:formTriggerEventTypes
  bind:formEndEventTypes
  bind:formCovers
  bind:formTriggerNotesContains
  categoryLabels={categoryLabels}
/>

<!-- Start Instance Dialog -->
<TrackerStartDialog
  bind:open={isStartDialogOpen}
  definition={startDefinition}
  history={historyInstances}
  onClose={() => startDefinition = null}
  onStart={() => startDefinition = null}
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
  }}
/>

<!-- Reservoir Report Dialog -->
<ReservoirReportDialog bind:open={isReservoirReportDialogOpen} defaultKind={ReservoirReportKind.Fill} />

<!-- Delete Definition Confirmation Dialog -->
<ConfirmDialog
  bind:open={isDeleteDefinitionDialogOpen}
  onOpenChange={(o) => { if (!o) deletingDefinitionId = null; }}
  title="Delete tracker definition"
  confirmLabel="Delete"
  destructive
  onConfirm={confirmDeleteDefinition}
>
  {#snippet description()}
    Are you sure you want to delete this tracker definition? This action
    cannot be undone. Any active instances using this definition will
    remain, but you will not be able to start new ones.
  {/snippet}
</ConfirmDialog>

<!-- Delete Instance Confirmation Dialog -->
<ConfirmDialog
  bind:open={isDeleteInstanceDialogOpen}
  onOpenChange={(o) => { if (!o) deletingInstanceId = null; }}
  title="Delete tracker instance"
  confirmLabel="Delete"
  destructive
  onConfirm={confirmDeleteInstance}
>
  {#snippet description()}
    Are you sure you want to delete this tracker instance? This action
    cannot be undone.
  {/snippet}
</ConfirmDialog>

<!-- Preset Dialog -->
<Dialog.Root bind:open={isPresetDialogOpen}>
  <Dialog.Content class="sm:max-w-[425px]">
    <Dialog.Header>
      <Dialog.Title>
        {isNewPreset ? "New preset" : "Edit preset"}
      </Dialog.Title>
      <Dialog.Description>
        Create a quick preset for one-step tracker activation.
      </Dialog.Description>
    </Dialog.Header>
    <div class="grid gap-4 py-4">
      <div class="space-y-2">
        <Label for="presetName">Preset name</Label>
        <Input
          id="presetName"
          bind:value={formPresetName}
          placeholder="e.g., G7 Sensor (Left Arm)"
        />
      </div>
      <div class="space-y-2">
        <Label for="presetDefinition">Tracker definition</Label>
        <Select.Root type="single" bind:value={formPresetDefinitionId}>
          <Select.Trigger>
            {definitions.find((d) => d.id === formPresetDefinitionId)?.name ??
              "Select a definition"}
          </Select.Trigger>
          <Select.Content>
            {#each definitions as def (def.id)}
              <Select.Item value={def.id ?? ""} label={def.name ?? ""} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="presetNotes">Default start notes (optional)</Label>
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
      <Button
        variant="outline"
        onclick={() => isPresetDialogOpen = false}
      >Cancel</Button>

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
<ConfirmDialog
  bind:open={isDeletePresetDialogOpen}
  onOpenChange={(o) => { if (!o) deletingPresetId = null; }}
  title="Delete preset"
  confirmLabel="Delete"
  destructive
  onConfirm={confirmDeletePreset}
>
  {#snippet description()}
    Are you sure you want to delete this preset? This action cannot be
    undone.
  {/snippet}
</ConfirmDialog>
