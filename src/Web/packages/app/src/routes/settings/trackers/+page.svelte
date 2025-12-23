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
  import type {
    TrackerDefinitionDto,
    TrackerInstanceDto,
    TrackerPresetDto,
    TrackerCategory,
    CompletionReason,
  } from "$lib/api/generated/nocturne-api-client";

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
  let formCategory = $state<TrackerCategory>(0); // Consumable
  let formIcon = $state("activity");
  let formLifespanHours = $state<number | undefined>(undefined);
  let formInfoHours = $state<number | undefined>(undefined);
  let formWarnHours = $state<number | undefined>(undefined);
  let formHazardHours = $state<number | undefined>(undefined);
  let formUrgentHours = $state<number | undefined>(undefined);
  let formIsFavorite = $state(false);

  // Start instance dialog
  let isStartDialogOpen = $state(false);
  let startDefinitionId = $state<string | null>(null);
  let startNotes = $state("");

  // Complete instance dialog
  let isCompleteDialogOpen = $state(false);
  let completingInstanceId = $state<string | null>(null);
  let completionReason = $state<CompletionReason>(0); // Completed
  let completionNotes = $state("");

  // Derived counts
  const activeCount = $derived(activeInstances.length);

  // Category labels
  const categoryLabels: Record<number, string> = {
    0: "Consumable",
    1: "Reservoir",
    2: "Appointment",
    3: "Reminder",
    4: "Custom",
  };

  // Completion reason labels
  const completionReasonLabels: Record<number, string> = {
    0: "Completed",
    1: "Expired",
    2: "Failed",
    3: "Fell Off",
    4: "Replaced Early",
    5: "Empty",
    6: "Refilled",
    7: "Attended",
    8: "Rescheduled",
    9: "Cancelled",
    10: "Missed",
    11: "Acknowledged",
    12: "Snoozed",
    13: "Other",
  };

  // Category icons and colors
  const categoryConfig: Record<
    number,
    { icon: typeof Activity; color: string }
  > = {
    0: { icon: Syringe, color: "text-blue-500" },
    1: { icon: Beaker, color: "text-purple-500" },
    2: { icon: Calendar, color: "text-green-500" },
    3: { icon: Clock, color: "text-orange-500" },
    4: { icon: Activity, color: "text-gray-500" },
  };

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [defs, active, history, prsts] = await Promise.all([
        trackersRemote.getDefinitions(),
        trackersRemote.getActiveInstances(),
        trackersRemote.getInstanceHistory(),
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
  ): "info" | "warn" | "hazard" | "urgent" | null {
    const def = definitions.find((d) => d.id === instance.definitionId);
    if (!def || !instance.ageHours) return null;
    if (def.urgentHours && instance.ageHours >= def.urgentHours)
      return "urgent";
    if (def.hazardHours && instance.ageHours >= def.hazardHours)
      return "hazard";
    if (def.warnHours && instance.ageHours >= def.warnHours) return "warn";
    if (def.infoHours && instance.ageHours >= def.infoHours) return "info";
    return null;
  }

  // Level styling
  function getLevelStyle(level: string | null): string {
    switch (level) {
      case "urgent":
        return "border-red-500 bg-red-500/10";
      case "hazard":
        return "border-orange-500 bg-orange-500/10";
      case "warn":
        return "border-yellow-500 bg-yellow-500/10";
      case "info":
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
    formCategory = 0;
    formIcon = "activity";
    formLifespanHours = undefined;
    formInfoHours = undefined;
    formWarnHours = undefined;
    formHazardHours = undefined;
    formUrgentHours = undefined;
    formIsFavorite = false;
    isDefinitionDialogOpen = true;
  }

  function openEditDefinition(def: TrackerDefinitionDto) {
    isNewDefinition = false;
    editingDefinition = def;
    formName = def.name || "";
    formDescription = def.description || "";
    formCategory = def.category ?? 0;
    formIcon = def.icon || "activity";
    formLifespanHours = def.lifespanHours;
    formInfoHours = def.infoHours;
    formWarnHours = def.warnHours;
    formHazardHours = def.hazardHours;
    formUrgentHours = def.urgentHours;
    formIsFavorite = def.isFavorite ?? false;
    isDefinitionDialogOpen = true;
  }

  // Save definition
  async function saveDefinition() {
    try {
      const data = {
        name: formName,
        description: formDescription || undefined,
        category: formCategory,
        icon: formIcon,
        lifespanHours: formLifespanHours,
        infoHours: formInfoHours,
        warnHours: formWarnHours,
        hazardHours: formHazardHours,
        urgentHours: formUrgentHours,
        isFavorite: formIsFavorite,
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
  function openStartDialog(definitionId: string) {
    startDefinitionId = definitionId;
    startNotes = "";
    isStartDialogOpen = true;
  }

  async function startInstanceHandler() {
    if (!startDefinitionId) return;
    try {
      await trackersRemote.startInstance({
        definitionId: startDefinitionId,
        startNotes: startNotes || undefined,
      });
      isStartDialogOpen = false;
      await loadData();
    } catch (err) {
      console.error("Failed to start instance:", err);
    }
  }

  // Complete instance
  function openCompleteDialog(instanceId: string) {
    completingInstanceId = instanceId;
    completionReason = 0;
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
                        {completionReasonLabels[instance.completionReason ?? 0]}
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
                    categoryConfig[def.category ?? 0]?.icon || Activity}
                  <div
                    class="flex items-center justify-between p-4 rounded-lg border"
                  >
                    <div class="flex items-center gap-3">
                      <div
                        class={cn(
                          "p-2 rounded-lg bg-muted",
                          categoryConfig[def.category ?? 0]?.color
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
                          {categoryLabels[def.category ?? 0]}
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
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title>
        {isNewDefinition ? "New Tracker Definition" : "Edit Definition"}
      </Dialog.Title>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="name">Name</Label>
        <Input id="name" bind:value={formName} placeholder="e.g., G7 Sensor" />
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
        <Label for="category">Category</Label>
        <Select.Root type="single" bind:value={formCategory}>
          <Select.Trigger>{categoryLabels[formCategory]}</Select.Trigger>
          <Select.Content>
            <Select.Item value={0} label="Consumable" />
            <Select.Item value={1} label="Reservoir" />
            <Select.Item value={2} label="Appointment" />
            <Select.Item value={3} label="Reminder" />
            <Select.Item value={4} label="Custom" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="lifespan">Expected Lifespan (hours)</Label>
          <Input
            id="lifespan"
            type="number"
            bind:value={formLifespanHours}
            placeholder="e.g., 240"
          />
        </div>
        <div class="space-y-2">
          <Label for="warn">Warn at (hours)</Label>
          <Input
            id="warn"
            type="number"
            bind:value={formWarnHours}
            placeholder="e.g., 192"
          />
        </div>
      </div>
      <div class="grid grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="hazard">Hazard at (hours)</Label>
          <Input
            id="hazard"
            type="number"
            bind:value={formHazardHours}
            placeholder="optional"
          />
        </div>
        <div class="space-y-2">
          <Label for="urgent">Urgent at (hours)</Label>
          <Input
            id="urgent"
            type="number"
            bind:value={formUrgentHours}
            placeholder="optional"
          />
        </div>
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
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Start Tracker</Dialog.Title>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="startNotes">Notes (optional)</Label>
        <Input
          id="startNotes"
          bind:value={startNotes}
          placeholder="e.g., Left arm, Lot #12345"
        />
      </div>
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
            <Select.Item value={0} label="Completed" />
            <Select.Item value={1} label="Expired" />
            <Select.Item value={2} label="Failed" />
            <Select.Item value={3} label="Fell Off" />
            <Select.Item value={4} label="Replaced Early" />
            <Select.Item value={5} label="Empty" />
            <Select.Item value={6} label="Refilled" />
            <Select.Item value={7} label="Attended" />
            <Select.Item value={8} label="Rescheduled" />
            <Select.Item value={9} label="Cancelled" />
            <Select.Item value={10} label="Missed" />
            <Select.Item value={13} label="Other" />
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
