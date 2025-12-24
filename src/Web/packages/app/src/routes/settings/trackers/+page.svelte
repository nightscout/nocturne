<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { Textarea } from "$lib/components/ui/textarea";
  import { DurationInput } from "$lib/components/ui/duration-input";
  import {
    TrackerNotificationEditor,
    type TrackerNotification,
  } from "$lib/components/trackers";
  import {
    Timer,
    Plus,
    Play,
    Check,
    Clock,
    AlertTriangle,
    History,
    Settings2,
    Bookmark,
    Trash2,
    Pencil,
    Loader2,
    Syringe,
    Activity,
    Calendar,
    Beaker,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import * as trackersRemote from "$lib/data/trackers.remote";
  import {
    NotificationUrgency,
    TrackerCategory,
    CompletionReason,
    DashboardVisibility,
    type TrackerDefinitionDto,
    type TrackerInstanceDto,
    type TrackerPresetDto,
  } from "$api";

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

  // Helper to convert API format to notifications array
  function definitionToNotifications(
    def: TrackerDefinitionDto
  ): TrackerNotification[] {
    const notifications: TrackerNotification[] = [];
    if (def.infoHours !== undefined && def.infoHours !== null) {
      notifications.push({
        urgency: NotificationUrgency.Info,
        hours: def.infoHours,
        description: "",
      });
    }
    if (def.warnHours !== undefined && def.warnHours !== null) {
      notifications.push({
        urgency: NotificationUrgency.Warn,
        hours: def.warnHours,
        description: "",
      });
    }
    if (def.hazardHours !== undefined && def.hazardHours !== null) {
      notifications.push({
        urgency: NotificationUrgency.Hazard,
        hours: def.hazardHours,
        description: "",
      });
    }
    if (def.urgentHours !== undefined && def.urgentHours !== null) {
      notifications.push({
        urgency: NotificationUrgency.Urgent,
        hours: def.urgentHours,
        description: "",
      });
    }

    // New format: use notificationThresholds array if available
    if (def.notificationThresholds && def.notificationThresholds.length > 0) {
      return def.notificationThresholds.map((t, i) => ({
        id: t.id,
        urgency: t.urgency ?? NotificationUrgency.Info,
        hours: t.hours,
        description: t.description ?? "",
        displayOrder: t.displayOrder ?? i,
      }));
    }

    return notifications;
  }

  // Helper to convert notifications array to API format
  function notificationsToApiFormat(notifications: TrackerNotification[]) {
    return notifications
      .filter((n) => n.hours !== undefined)
      .map((n, i) => ({
        urgency: n.urgency,
        hours: n.hours!,
        description: n.description || undefined,
        displayOrder: n.displayOrder ?? i,
      }));
  }

  // Start instance dialog
  // Start instance
  let isStartDialogOpen = $state(false);
  let startDefinitionId = $state<string | null>(null);
  let startNotes = $state("");
  let startedAtString = $state(""); // YYYY-MM-DDTHH:mm

  // Initialize dialog with current local time
  function openStartDialog(definitionId: string) {
    startDefinitionId = definitionId;
    startNotes = "";

    // Default to now, formatted for datetime-local
    const now = new Date();
    // Offset for local timezone
    const offset = now.getTimezoneOffset() * 60000;
    const localIso = new Date(now.getTime() - offset)
      .toISOString()
      .slice(0, 16);
    startedAtString = localIso;

    isStartDialogOpen = true;
  }

  async function startInstanceHandler() {
    if (!startDefinitionId) return;
    try {
      const startedAt = startedAtString ? new Date(startedAtString) : undefined;
      await trackersRemote.startInstance({
        definitionId: startDefinitionId,
        startNotes: startNotes || undefined,
        startedAt: startedAt,
      });
      isStartDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to start instance:", err);
    }
  }

  // Derived for Start Dialog
  const startDefinition = $derived(
    definitions.find((d) => d.id === startDefinitionId)
  );

  const startPreview = $derived.by(() => {
    if (
      !startDefinition ||
      !startDefinition.notificationThresholds ||
      !startedAtString
    )
      return [];

    const start = new Date(startedAtString);
    const now = new Date();

    // Safety check for invalid date
    if (isNaN(start.getTime())) return [];

    return startDefinition.notificationThresholds
      .filter((n) => n.hours !== undefined)
      .map((n) => {
        const triggerTime = new Date(
          start.getTime() + n.hours! * 60 * 60 * 1000
        );
        const timeUntil = triggerTime.getTime() - now.getTime();
        const hoursUntil = timeUntil / (1000 * 60 * 60);

        return {
          ...n,
          triggerTime,
          isPast: timeUntil < 0,
          relativeTime:
            Math.abs(hoursUntil) < 1
              ? `${Math.abs(Math.round(hoursUntil * 60))} mins`
              : `${Math.abs(hoursUntil).toFixed(1)} hours`,
        };
      })
      .sort((a, b) => (a.hours ?? 0) - (b.hours ?? 0));
  });

  // Complete instance dialog
  let isCompleteDialogOpen = $state(false);
  let completingInstanceId = $state<string | null>(null);
  let completionReason = $state<CompletionReason>(CompletionReason.Completed);
  let completionNotes = $state("");

  // Derived counts
  const activeCount = $derived(activeInstances.length);

  // Category labels
  const categoryLabels: Record<TrackerCategory, string> = {
    [TrackerCategory.Consumable]: "Consumable",
    [TrackerCategory.Reservoir]: "Reservoir",
    [TrackerCategory.Appointment]: "Appointment",
    [TrackerCategory.Reminder]: "Reminder",
    [TrackerCategory.Custom]: "Custom",
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

  // Category icons and colors
  const categoryConfig: Record<
    TrackerCategory,
    { icon: typeof Activity; color: string }
  > = {
    [TrackerCategory.Consumable]: { icon: Syringe, color: "text-blue-500" },
    [TrackerCategory.Reservoir]: { icon: Beaker, color: "text-purple-500" },
    [TrackerCategory.Appointment]: { icon: Calendar, color: "text-green-500" },
    [TrackerCategory.Reminder]: { icon: Clock, color: "text-orange-500" },
    [TrackerCategory.Custom]: { icon: Activity, color: "text-gray-500" },
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

  // Initial load
  $effect(() => {
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
  function formatDate(dateStr: Date | undefined): string {
    if (!dateStr) return "";
    return new Date(dateStr).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Get notification level for instance
  function getInstanceLevel(
    instance: TrackerInstanceDto
  ): NotificationUrgency | null {
    const def = definitions.find((d) => d.id === instance.definitionId);
    if (!def || !instance.ageHours) return null;
    if (def.urgentHours && instance.ageHours >= def.urgentHours)
      return NotificationUrgency.Urgent;
    if (def.hazardHours && instance.ageHours >= def.hazardHours)
      return NotificationUrgency.Hazard;
    if (def.warnHours && instance.ageHours >= def.warnHours)
      return NotificationUrgency.Warn;
    if (def.infoHours && instance.ageHours >= def.infoHours)
      return NotificationUrgency.Info;
    return null;
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

  // Open definition dialog
  function openNewDefinition() {
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
    isDefinitionDialogOpen = true;
  }

  function openEditDefinition(def: TrackerDefinitionDto) {
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
    isDefinitionDialogOpen = true;
  }

  // Save definition
  async function saveDefinition() {
    try {
      const notificationThresholds =
        notificationsToApiFormat(formNotifications);
      const data = {
        name: formName,
        description: formDescription || undefined,
        category: formCategory,
        icon: formIcon,
        lifespanHours: formLifespanHours,
        notificationThresholds: notificationThresholds,
        isFavorite: formIsFavorite,
        dashboardVisibility: formDashboardVisibility,
      };

      if (isNewDefinition) {
        await trackersRemote.createDefinition(data);
      } else if (editingDefinition) {
        await trackersRemote.updateDefinition({
          id: editingDefinition.id!,
          request: data,
        });
      }
      isDefinitionDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to save definition:", err);
    }
  }

  // Delete definition
  async function deleteDefinitionHandler(id: string) {
    if (!confirm("Delete this tracker definition?")) return;
    try {
      await trackersRemote.deleteDefinition(id);
      await loadData();
    } catch (err) {
      console.error("Failed to delete definition:", err);
    }
  }

  // Start instance

  // Complete instance
  function openCompleteDialog(instanceId: string) {
    completingInstanceId = instanceId;
    completionReason = CompletionReason.Completed;
    completionNotes = "";
    isCompleteDialogOpen = true;
  }

  async function completeInstanceHandler() {
    if (!completingInstanceId) return;
    try {
      await trackersRemote.completeInstance({
        id: completingInstanceId,
        request: {
          reason: completionReason,
          completionNotes: completionNotes || undefined,
        },
      });
      isCompleteDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to complete instance:", err);
    }
  }

  // Delete instance
  async function deleteInstanceHandler(id: string) {
    if (!confirm("Delete this tracker instance?")) return;
    try {
      await trackersRemote.deleteInstance(id);
      await loadData();
    } catch (err) {
      console.error("Failed to delete instance:", err);
    }
  }

  // Apply preset
  async function applyPresetHandler(presetId: string) {
    try {
      await trackersRemote.applyPreset({ id: presetId });
      await loadData();
    } catch (err) {
      console.error("Failed to apply preset:", err);
    }
  }
</script>

<svelte:head>
  <title>Trackers - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center gap-3 mb-2">
      <div
        class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
      >
        <Timer class="h-5 w-5 text-primary" />
      </div>
      <div>
        <h1 class="text-3xl font-bold tracking-tight">Trackers</h1>
        <p class="text-muted-foreground">
          Track consumables, appointments, and reminders
        </p>
      </div>
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
      <Tabs.Content value="active">
        <Card>
          <CardHeader class="flex flex-row items-center justify-between">
            <div>
              <CardTitle>Active Trackers</CardTitle>
              <CardDescription>
                Currently running tracker instances
              </CardDescription>
            </div>
            {#if definitions.length > 0}
              <Select.Root type="single">
                <Select.Trigger class="w-[200px]">
                  <Plus class="h-4 w-4 mr-2" />
                  Start Tracker
                </Select.Trigger>
                <Select.Content>
                  {#each definitions as def}
                    <Select.Item
                      value={def.id ?? ""}
                      label={def.name ?? ""}
                      onclick={() => openStartDialog(def.id!)}
                    />
                  {/each}
                </Select.Content>
              </Select.Root>
            {/if}
          </CardHeader>
          <CardContent>
            {#if activeInstances.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <Timer class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No active trackers</p>
                <p class="text-sm">Start a tracker from your definitions</p>
              </div>
            {:else}
              <div class="space-y-3">
                {#each activeInstances as instance}
                  {@const level = getInstanceLevel(instance)}
                  <div
                    class={cn(
                      "flex items-center justify-between p-4 rounded-lg border",
                      getLevelStyle(level)
                    )}
                  >
                    <div class="flex items-center gap-3">
                      <div class="text-2xl font-bold tabular-nums">
                        {formatAge(instance.ageHours ?? 0)}
                      </div>
                      <div>
                        <div class="font-medium">{instance.definitionName}</div>
                        <div class="text-sm text-muted-foreground">
                          Started {formatDate(instance.startedAt)}
                          {#if instance.startNotes}
                            · {instance.startNotes}
                          {/if}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        onclick={() => openCompleteDialog(instance.id!)}
                      >
                        <Check class="h-4 w-4 mr-1" />
                        Complete
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onclick={() => deleteInstanceHandler(instance.id!)}
                      >
                        <Trash2 class="h-4 w-4" />
                      </Button>
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- History Tab -->
      <Tabs.Content value="history">
        <Card>
          <CardHeader>
            <CardTitle>History</CardTitle>
            <CardDescription>Completed tracker instances</CardDescription>
          </CardHeader>
          <CardContent>
            {#if historyInstances.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <History class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No history yet</p>
              </div>
            {:else}
              <div class="space-y-2">
                {#each historyInstances as instance}
                  <div
                    class="flex items-center justify-between p-3 rounded-lg border"
                  >
                    <div>
                      <div class="font-medium">{instance.definitionName}</div>
                      <div class="text-sm text-muted-foreground">
                        {formatAge(instance.ageHours ?? 0)} ·
                        {completionReasonLabels[
                          instance.completionReason ??
                            CompletionReason.Completed
                        ]}
                        {#if instance.completionNotes}
                          · {instance.completionNotes}
                        {/if}
                      </div>
                    </div>
                    <div class="text-sm text-muted-foreground">
                      {formatDate(instance.completedAt ?? instance.startedAt)}
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Definitions Tab -->
      <Tabs.Content value="definitions">
        <Card>
          <CardHeader class="flex flex-row items-center justify-between">
            <div>
              <CardTitle>Tracker Definitions</CardTitle>
              <CardDescription>
                Reusable tracker templates with notification thresholds
              </CardDescription>
            </div>
            <Button onclick={openNewDefinition}>
              <Plus class="h-4 w-4 mr-2" />
              New Definition
            </Button>
          </CardHeader>
          <CardContent>
            {#if definitions.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <Settings2 class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No definitions yet</p>
                <p class="text-sm">
                  Create a tracker definition to get started
                </p>
              </div>
            {:else}
              <div class="space-y-3">
                {#each definitions as def}
                  {@const Cat =
                    categoryConfig[def.category ?? TrackerCategory.Consumable]
                      ?.icon || Activity}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border"
                  >
                    <div class="flex items-center gap-3">
                      <div
                        class={cn(
                          "p-2 rounded-lg bg-muted",
                          categoryConfig[
                            def.category ?? TrackerCategory.Consumable
                          ]?.color
                        )}
                      >
                        <Cat class="h-5 w-5" />
                      </div>
                      <div>
                        <div class="font-medium flex items-center gap-2">
                          {def.name}
                          {#if def.isFavorite}
                            <Badge variant="secondary">★ Favorite</Badge>
                          {/if}
                        </div>
                        <div class="text-sm text-muted-foreground">
                          {categoryLabels[
                            def.category ?? TrackerCategory.Consumable
                          ]}
                          {#if def.lifespanHours}
                            · {def.lifespanHours}h lifespan
                          {/if}
                          {#if def.warnHours}
                            · Warn at {def.warnHours}h
                          {/if}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-2">
                      <Button
                        variant="outline"
                        size="sm"
                        onclick={() => openStartDialog(def.id!)}
                      >
                        <Play class="h-4 w-4 mr-1" />
                        Start
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onclick={() => openEditDefinition(def)}
                      >
                        <Pencil class="h-4 w-4" />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon"
                        onclick={() => deleteDefinitionHandler(def.id!)}
                      >
                        <Trash2 class="h-4 w-4" />
                      </Button>
                    </div>
                  </div>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>

      <!-- Presets Tab -->
      <Tabs.Content value="presets">
        <Card>
          <CardHeader>
            <CardTitle>Quick Presets</CardTitle>
            <CardDescription>One-click tracker activation</CardDescription>
          </CardHeader>
          <CardContent>
            {#if presets.length === 0}
              <div class="text-center py-8 text-muted-foreground">
                <Bookmark class="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p>No presets yet</p>
                <p class="text-sm">
                  Create presets from the API for quick access
                </p>
              </div>
            {:else}
              <div class="grid gap-3 md:grid-cols-2">
                {#each presets as preset}
                  <Button
                    variant="outline"
                    class="h-auto p-4 justify-start"
                    onclick={() => applyPresetHandler(preset.id!)}
                  >
                    <div class="text-left">
                      <div class="font-medium">{preset.name}</div>
                      <div class="text-sm text-muted-foreground">
                        {preset.definitionName}
                        {#if preset.defaultStartNotes}
                          · {preset.defaultStartNotes}
                        {/if}
                      </div>
                    </div>
                  </Button>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      </Tabs.Content>
    </Tabs.Root>
  {/if}
</div>

<!-- Definition Dialog -->
<Dialog.Root bind:open={isDefinitionDialogOpen}>
  <Dialog.Content class="max-w-2xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>
        {isNewDefinition ? "New Tracker Definition" : "Edit Definition"}
      </Dialog.Title>
    </Dialog.Header>
    <div class="space-y-6 py-4">
      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="name">Name</Label>
          <Input
            id="name"
            bind:value={formName}
            placeholder="e.g., G7 Sensor"
          />
        </div>
        <div class="space-y-2">
          <Label for="category">Category</Label>
          <Select.Root type="single" bind:value={formCategory}>
            <Select.Trigger>{categoryLabels[formCategory]}</Select.Trigger>
            <Select.Content>
              <Select.Item
                value={TrackerCategory.Consumable}
                label="Consumable"
              />
              <Select.Item
                value={TrackerCategory.Reservoir}
                label="Reservoir"
              />
              <Select.Item
                value={TrackerCategory.Appointment}
                label="Appointment"
              />
              <Select.Item value={TrackerCategory.Reminder} label="Reminder" />
              <Select.Item value={TrackerCategory.Custom} label="Custom" />
            </Select.Content>
          </Select.Root>
        </div>
      </div>

      <div class="space-y-2">
        <Label for="description">Description (optional)</Label>
        <Input
          id="description"
          bind:value={formDescription}
          placeholder="Optional description"
        />
      </div>

      <div class="space-y-2">
        <Label for="lifespan">Expected Lifespan</Label>
        <DurationInput
          id="lifespan"
          bind:value={formLifespanHours}
          placeholder="e.g., 10x24 or 10d"
        />
      </div>

      <TrackerNotificationEditor bind:notifications={formNotifications} />

      <div class="space-y-2">
        <Label for="dashboardVisibility">Dashboard Visibility</Label>
        <Select.Root type="single" bind:value={formDashboardVisibility}>
          <Select.Trigger>
            {#if formDashboardVisibility === DashboardVisibility.Off}
              Off - Don't show on dashboard
            {:else if formDashboardVisibility === DashboardVisibility.Always}
              Always show
            {:else if formDashboardVisibility === DashboardVisibility.Info}
              Show after Info threshold
            {:else if formDashboardVisibility === DashboardVisibility.Warn}
              Show after Warn threshold
            {:else if formDashboardVisibility === DashboardVisibility.Hazard}
              Show after Hazard threshold
            {:else if formDashboardVisibility === DashboardVisibility.Urgent}
              Show after Urgent threshold
            {:else}
              Always show
            {/if}
          </Select.Trigger>
          <Select.Content>
            <Select.Item
              value={DashboardVisibility.Off}
              label="Off - Don't show on dashboard"
            />
            <Select.Item
              value={DashboardVisibility.Always}
              label="Always show"
            />
            <Select.Item
              value={DashboardVisibility.Info}
              label="Show after Info threshold"
            />
            <Select.Item
              value={DashboardVisibility.Warn}
              label="Show after Warn threshold"
            />
            <Select.Item
              value={DashboardVisibility.Hazard}
              label="Show after Hazard threshold"
            />
            <Select.Item
              value={DashboardVisibility.Urgent}
              label="Show after Urgent threshold"
            />
          </Select.Content>
        </Select.Root>
        <p class="text-xs text-muted-foreground">
          When to show this tracker as a pill on the dashboard
        </p>
      </div>
    </div>

    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={() => (isDefinitionDialogOpen = false)}
      >
        Cancel
      </Button>
      <Button onclick={saveDefinition} disabled={!formName}>Save</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Start Instance Dialog -->
<Dialog.Root bind:open={isStartDialogOpen}>
  <Dialog.Content class="sm:max-w-[425px]">
    <Dialog.Header>
      <Dialog.Title>Start Tracker</Dialog.Title>
      <Dialog.Description>
        Begin tracking {startDefinition?.name ?? "tracker"}.
      </Dialog.Description>
    </Dialog.Header>
    <div class="grid gap-4 py-4">
      <div class="space-y-2">
        <Label for="startedAt">Start Time</Label>
        <Input
          type="datetime-local"
          id="startedAt"
          bind:value={startedAtString}
        />
        <p class="text-[10px] text-muted-foreground">
          Adjust if you started this earlier.
        </p>
      </div>

      <div class="space-y-2">
        <Label for="startNotes">Notes (optional)</Label>
        <Input
          id="startNotes"
          bind:value={startNotes}
          placeholder="e.g., Left arm, Lot #12345"
        />
      </div>

      {#if startPreview.length > 0}
        <div class="rounded-lg border bg-muted/50 p-3 mt-2">
          <Label class="text-xs mb-2 block font-medium">
            Notification Schedule (Adjusted)
          </Label>
          <div class="space-y-2">
            {#each startPreview as preview}
              {@const isPast = preview.isPast}
              {@const urgencyLower = String(preview.urgency).toLowerCase()}
              <div class="flex items-center justify-between text-xs">
                <div class="flex items-center gap-2">
                  <div
                    class={cn(
                      "w-2 h-2 rounded-full",
                      (urgencyLower === "info" || urgencyLower === "0") &&
                        "bg-blue-500",
                      (urgencyLower === "warn" || urgencyLower === "1") &&
                        "bg-yellow-500",
                      (urgencyLower === "hazard" || urgencyLower === "2") &&
                        "bg-orange-500",
                      (urgencyLower === "urgent" || urgencyLower === "3") &&
                        "bg-red-500"
                    )}
                  ></div>
                  <span>{preview.hours}h</span>
                </div>
                <div
                  class={cn(
                    "flex flex-col items-end",
                    isPast ? "text-destructive" : "text-muted-foreground"
                  )}
                >
                  <span>
                    {isPast ? "Triggered" : "Triggering in"}
                    {preview.relativeTime}
                  </span>
                  <span class="text-[10px] opacity-70">
                    {preview.triggerTime.toLocaleTimeString()}
                  </span>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isStartDialogOpen = false)}>
        Cancel
      </Button>
      <Button onclick={startInstanceHandler}>
        <Play class="h-4 w-4 mr-2" />
        Start
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Complete Instance Dialog -->
<Dialog.Root bind:open={isCompleteDialogOpen}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Complete Tracker</Dialog.Title>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="reason">Completion Reason</Label>
        <Select.Root type="single" bind:value={completionReason}>
          <Select.Trigger>
            {completionReasonLabels[completionReason]}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value={CompletionReason.Completed} label="Completed" />
            <Select.Item value={CompletionReason.Expired} label="Expired" />
            <Select.Item value={CompletionReason.Failed} label="Failed" />
            <Select.Item value={CompletionReason.FellOff} label="Fell Off" />
            <Select.Item
              value={CompletionReason.ReplacedEarly}
              label="Replaced Early"
            />
            <Select.Item value={CompletionReason.Empty} label="Empty" />
            <Select.Item value={CompletionReason.Refilled} label="Refilled" />
            <Select.Item value={CompletionReason.Attended} label="Attended" />
            <Select.Item
              value={CompletionReason.Rescheduled}
              label="Rescheduled"
            />
            <Select.Item value={CompletionReason.Cancelled} label="Cancelled" />
            <Select.Item value={CompletionReason.Missed} label="Missed" />
            <Select.Item value={CompletionReason.Other} label="Other" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="completionNotes">Notes (optional)</Label>
        <Textarea
          id="completionNotes"
          bind:value={completionNotes}
          placeholder="e.g., Sensor error E2 on day 8"
        />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (isCompleteDialogOpen = false)}>
        Cancel
      </Button>
      <Button onclick={completeInstanceHandler}>
        <Check class="h-4 w-4 mr-2" />
        Complete
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
