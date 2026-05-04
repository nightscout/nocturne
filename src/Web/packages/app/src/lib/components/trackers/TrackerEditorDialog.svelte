<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { DurationInput } from "$lib/components/ui/duration-input";
  import { TrackerNotificationEditor, type TrackerNotification } from "$lib/components/trackers";
  import EventTypeCombobox from "$lib/components/treatments/EventTypeCombobox.svelte";
  import * as trackersRemote from "$api/generated/trackers.generated.remote";
  import { tick } from "svelte";
  import {
    TrackerCategory,
    DashboardVisibility,
    TrackerVisibility,
    TrackerMode,
    type TrackerDefinitionDto,
  } from "$api";

  const createRemote = trackersRemote.createDefinition;
  const updateRemote = trackersRemote.updateDefinition;

  let {
    open = $bindable(false),
    isNewDefinition,
    editingDefinition,
    formName = $bindable(""),
    formDescription = $bindable(""),
    formCategory = $bindable(TrackerCategory.Consumable),
    formIcon = $bindable("activity"),
    formLifespanHours = $bindable(undefined),
    formNotifications = $bindable([]),
    formIsFavorite = $bindable(false),
    formDashboardVisibility = $bindable(DashboardVisibility.Always),
    formVisibility = $bindable(TrackerVisibility.Public),
    formMode = $bindable(TrackerMode.Duration),
    formStartEventType = $bindable(undefined),
    formCompletionEventType = $bindable(undefined),
    categoryLabels,
    loadData,
  } = $props<{
    open?: boolean;
    isNewDefinition: boolean;
    editingDefinition: TrackerDefinitionDto | null;
    formName?: string;
    formDescription?: string;
    formCategory?: TrackerCategory;
    formIcon?: string;
    formLifespanHours?: number | undefined;
    formNotifications?: TrackerNotification[];
    formIsFavorite?: boolean;
    formDashboardVisibility?: DashboardVisibility;
    formVisibility?: TrackerVisibility;
    formMode?: TrackerMode;
    formStartEventType?: string | undefined;
    formCompletionEventType?: string | undefined;
    categoryLabels: Record<TrackerCategory, string>;
    loadData: () => Promise<void>;
  }>();

  const createForm = $derived(createRemote.for("create"));
  const updateForm = $derived(updateRemote.for(editingDefinition?.id ?? ""));

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
</script>

{#snippet definitionFormFields(prefix: string)}
  <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
    <div class="space-y-2">
      <Label for="name">Name</Label>
      <Input
        id="name"
        name="{prefix}name"
        bind:value={formName}
        placeholder="e.g., G7 Sensor"
      />
    </div>
    <div class="space-y-2">
      <Label for="category">Category</Label>
      <Select.Root
        type="single"
        name="{prefix}category"
        bind:value={formCategory}
      >
        <Select.Trigger>{categoryLabels[formCategory]}</Select.Trigger>
        <Select.Content>
          <Select.Item value={TrackerCategory.Sensor} label="Sensor" />
          <Select.Item value={TrackerCategory.Cannula} label="Cannula" />
          <Select.Item value={TrackerCategory.Battery} label="Battery" />
          <Select.Item value={TrackerCategory.Reservoir} label="Reservoir" />
          <Select.Item
            value={TrackerCategory.Appointment}
            label="Appointment"
          />
          <Select.Item value={TrackerCategory.Reminder} label="Reminder" />
          <Select.Item value={TrackerCategory.Consumable} label="Consumable" />
          <Select.Item value={TrackerCategory.Custom} label="Custom" />
        </Select.Content>
      </Select.Root>
    </div>
  </div>

  <div class="space-y-2">
    <Label for="description">Description (optional)</Label>
    <Input
      id="description"
      name="{prefix}description"
      bind:value={formDescription}
      placeholder="Optional description"
    />
  </div>

  <div class="space-y-2">
    <Label>Tracker Mode</Label>
    <Select.Root type="single" name="{prefix}mode" bind:value={formMode}>
      <Select.Trigger>
        {#if formMode === TrackerMode.Duration}
          Duration - runs for a time period
        {:else}
          Event - scheduled for specific datetime
        {/if}
      </Select.Trigger>
      <Select.Content>
        <Select.Item
          value={TrackerMode.Duration}
          label="Duration - runs for a time period"
        />
        <Select.Item
          value={TrackerMode.Event}
          label="Event - scheduled for specific datetime"
        />
      </Select.Content>
    </Select.Root>
    <p class="text-xs text-muted-foreground">
      {#if formMode === TrackerMode.Duration}
        Duration trackers run from a start time for a specified lifespan.
      {:else}
        Event trackers are scheduled for a specific date and time.
      {/if}
    </p>
  </div>

  {#if formMode === TrackerMode.Duration}
    <div class="space-y-2">
      <Label for="lifespan">Expected Lifespan</Label>
      <DurationInput
        id="lifespan"
        bind:value={formLifespanHours}
        placeholder="e.g., 10x24 or 10d"
      />
      <input
        type="hidden"
        name="n:{prefix}lifespanHours"
        value={formLifespanHours ?? ""}
      />
    </div>
  {/if}

  <TrackerNotificationEditor
    bind:notifications={formNotifications}
    mode={formMode === TrackerMode.Duration ? "Duration" : "Event"}
    lifespanHours={formMode === TrackerMode.Duration
      ? formLifespanHours
      : undefined}
  />

  {#each notificationsToApiFormat(formNotifications) as threshold, i}
    <input
      type="hidden"
      name="{prefix}notificationThresholds[{i}].urgency"
      value={threshold.urgency}
    />
    <input
      type="hidden"
      name="n:{prefix}notificationThresholds[{i}].hours"
      value={threshold.hours}
    />
    <input
      type="hidden"
      name="{prefix}notificationThresholds[{i}].description"
      value={threshold.description ?? ""}
    />
    <input
      type="hidden"
      name="n:{prefix}notificationThresholds[{i}].displayOrder"
      value={threshold.displayOrder ?? i}
    />
  {/each}

  <input
    type="hidden"
    name="b:{prefix}isFavorite"
    value={formIsFavorite ? "on" : ""}
  />
  <input type="hidden" name="{prefix}icon" value={formIcon} />
  <input
    type="hidden"
    name="{prefix}startEventType"
    value={formStartEventType ?? ""}
  />
  <input
    type="hidden"
    name="{prefix}completionEventType"
    value={formCompletionEventType ?? ""}
  />

  <div class="space-y-2">
    <Label for="dashboardVisibility">Dashboard Visibility</Label>
    <Select.Root
      type="single"
      name="{prefix}dashboardVisibility"
      bind:value={formDashboardVisibility}
    >
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
        <Select.Item value={DashboardVisibility.Always} label="Always show" />
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

  <div class="space-y-2">
    <Label for="visibility">Public Visibility</Label>
    <Select.Root
      type="single"
      name="{prefix}visibility"
      bind:value={formVisibility}
    >
      <Select.Trigger>
        {#if formVisibility === TrackerVisibility.Public}
          Public - Visible to everyone
        {:else if formVisibility === TrackerVisibility.Private}
          Private - Only you can see
        {:else}
          Public - Visible to everyone
        {/if}
      </Select.Trigger>
      <Select.Content>
        <Select.Item
          value={TrackerVisibility.Public}
          label="Public - Visible to everyone"
        />
        <Select.Item
          value={TrackerVisibility.Private}
          label="Private - Only you can see"
        />
      </Select.Content>
    </Select.Root>
    <p class="text-xs text-muted-foreground">
      Controls whether this tracker is visible to unauthenticated users
    </p>
  </div>

  <div class="space-y-3 pt-2 border-t">
    <Label class="text-sm font-medium">Event Integration (Nightscout)</Label>
    <p class="text-xs text-muted-foreground -mt-1">
      Optionally create treatment events when this tracker starts or completes.
      This maintains compatibility with existing CAGE/SAGE pills.
    </p>

    <div class="space-y-2">
      <Label for="startEventType" class="text-xs">Create event on start</Label>
      <EventTypeCombobox
        bind:value={formStartEventType}
        onSelect={(type) => (formStartEventType = type)}
        placeholder="None - don't create event"
      />
    </div>

    <div class="space-y-2">
      <Label for="completionEventType" class="text-xs">
        Create event on completion
      </Label>
      <EventTypeCombobox
        bind:value={formCompletionEventType}
        onSelect={(type) => (formCompletionEventType = type)}
        placeholder="None - don't create event"
      />
    </div>
  </div>
{/snippet}

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>
        {isNewDefinition ? "New Tracker Definition" : "Edit Definition"}
      </Dialog.Title>
    </Dialog.Header>

    {#if isNewDefinition}
      <form
        {...createForm.enhance(async ({ submit }) => {
          await submit();
          if (createForm.result) {
            await loadData();
            await tick();
            open = false;
          }
        })}
      >
        <div class="space-y-6 py-4">
          {@render definitionFormFields("")}
        </div>

        <Dialog.Footer>
          <Button
            type="button"
            variant="outline"
            onclick={() => (open = false)}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            disabled={!formName || !!createForm.pending}
          >
            {createForm.pending ? "Saving..." : "Save"}
          </Button>
        </Dialog.Footer>
      </form>
    {:else}
      <form
        {...updateForm.enhance(async ({ submit }) => {
          await submit();
          if (updateForm.result) {
            await loadData();
            await tick();
            open = false;
          }
        })}
      >
        <input type="hidden" name="id" value={editingDefinition?.id ?? ""} />
        <div class="space-y-6 py-4">
          {@render definitionFormFields("request.")}
        </div>

        <Dialog.Footer>
          <Button
            type="button"
            variant="outline"
            onclick={() => (open = false)}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            disabled={!formName || !!updateForm.pending}
          >
            {updateForm.pending ? "Saving..." : "Save"}
          </Button>
        </Dialog.Footer>
      </form>
    {/if}
  </Dialog.Content>
</Dialog.Root>