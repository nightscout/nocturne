<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import * as Select from "$lib/components/ui/select";
  import { DurationInput } from "$lib/components/ui/duration-input";
  import { TrackerNotificationEditor, type TrackerNotification } from "$lib/components/trackers";
  import EventTypeCombobox from "$lib/components/treatments/EventTypeCombobox.svelte";
  import { Artwork } from "water-ui";
  import { batteryArtwork } from "$lib/watercolour-icons";
  import * as trackersRemote from "$api/generated/trackers.generated.remote";
  import { tick } from "svelte";
  import {
    TrackerCategory,
    DashboardVisibility,
    TrackerMode,
    NotificationUrgency,
    PumpCapability,
    type TrackerDefinitionDto,
  } from "$api";

  const createRemote = trackersRemote.createDefinition;
  const updateRemote = trackersRemote.updateDefinition;

  interface Props {
    open?: boolean;
    isNewDefinition: boolean;
    editingDefinition: TrackerDefinitionDto | null;
    formName?: string;
    formDescription?: string;
    formCategory?: TrackerCategory;
    formIcon?: string;
    formLifespanHours?: number | undefined;
    formLowReservoirUnits?: number | undefined;
    formLowReservoirUrgency?: NotificationUrgency;
    formNotifications?: TrackerNotification[];
    formIsFavorite?: boolean;
    formDashboardVisibility?: DashboardVisibility;
    formMode?: TrackerMode;
    formStartEventType?: string | undefined;
    formCompletionEventType?: string | undefined;
    formTriggerEventTypes?: string[];
    formEndEventTypes?: string[];
    formCovers?: PumpCapability[];
    formTriggerNotesContains?: string;
    categoryLabels: Record<TrackerCategory, string>;
    loadData: () => Promise<void>;
  }

  let {
    open = $bindable(false),
    isNewDefinition,
    editingDefinition,
    formName = $bindable(""),
    formDescription = $bindable(""),
    formCategory = $bindable(TrackerCategory.Consumable),
    formIcon = $bindable("activity"),
    formLifespanHours = $bindable(undefined),
    formLowReservoirUnits = $bindable(undefined),
    formLowReservoirUrgency = $bindable(NotificationUrgency.Warn),
    formNotifications = $bindable([]),
    formIsFavorite = $bindable(false),
    formDashboardVisibility = $bindable(DashboardVisibility.Always),
    formMode = $bindable(TrackerMode.Duration),
    formStartEventType = $bindable(undefined),
    formCompletionEventType = $bindable(undefined),
    formTriggerEventTypes = $bindable([]),
    formEndEventTypes = $bindable([]),
    formCovers = $bindable([]),
    formTriggerNotesContains = $bindable(""),
    categoryLabels,
    loadData,
  }: Props = $props();

  const triggerEventTypesQuery = trackersRemote.getTriggerEventTypes();
  const triggerEventTypes = $derived(triggerEventTypesQuery.current ?? []);

  // A trigger only ever restarts a Duration tracker, and an empty list has to be posted as one
  // blank value: no field at all means "leave the stored triggers as they are".
  const postedTriggers = $derived(
    formMode === TrackerMode.Duration && formTriggerEventTypes.length > 0
      ? formTriggerEventTypes
      : [""]
  );

  function toggleTrigger(eventType: string, checked: boolean) {
    formTriggerEventTypes = checked
      ? [...formTriggerEventTypes, eventType]
      : formTriggerEventTypes.filter((t) => t !== eventType);
  }

  const endEventTypesQuery = trackersRemote.getEndEventTypes();
  const endEventTypes = $derived(endEventTypesQuery.current ?? []);

  // Posted like the triggers, for the same reason.
  const postedEnds = $derived(
    formMode === TrackerMode.Duration && formEndEventTypes.length > 0
      ? formEndEventTypes
      : [""]
  );

  function toggleEnd(eventType: string, checked: boolean) {
    formEndEventTypes = checked
      ? [...formEndEventTypes, eventType]
      : formEndEventTypes.filter((t) => t !== eventType);
  }

  const capabilityLabels: Record<PumpCapability, string> = {
    [PumpCapability.Site]: "Infusion site",
    [PumpCapability.Reservoir]: "Insulin",
    [PumpCapability.Power]: "Pump battery",
  };

  const capabilities = Object.values(PumpCapability);

  // Pump hardware only: a sensor, an appointment or a reminder replaces none of these.
  const showsCovers = $derived(
    formMode === TrackerMode.Duration &&
      formCategory !== TrackerCategory.Sensor &&
      formCategory !== TrackerCategory.Appointment &&
      formCategory !== TrackerCategory.Reminder
  );

  // Posted like the triggers, for the same reason.
  const postedCovers = $derived(
    showsCovers && formCovers.length > 0 ? formCovers : [""]
  );

  // A pod tracker that states nothing covers what a pod provides, as the server reads it.
  const holdsInsulin = $derived(
    formCategory === TrackerCategory.Reservoir ||
      (formCategory === TrackerCategory.Pod && formCovers.length === 0) ||
      (showsCovers && formCovers.includes(PumpCapability.Reservoir))
  );

  function toggleCover(capability: PumpCapability, checked: boolean) {
    formCovers = checked
      ? [...formCovers, capability]
      : formCovers.filter((c) => c !== capability);
  }

  // A pod is the site, the insulin and the battery together.
  function changeCategory(category: string) {
    if (category === TrackerCategory.Pod) formCovers = [...capabilities];
  }

  const followsEvents = $derived(
    formTriggerEventTypes.length > 0 || formEndEventTypes.length > 0
  );

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
  <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
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
        onValueChange={changeCategory}
      >
        <Select.Trigger data-testid="tracker-category-trigger">{categoryLabels[formCategory]}</Select.Trigger>
        <Select.Content>
          <Select.Item value={TrackerCategory.Sensor} label="Sensor" />
          <Select.Item value={TrackerCategory.Cannula} label="Cannula" />
          <Select.Item value={TrackerCategory.Pod} label="Pod" />
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

  {#if formCategory === TrackerCategory.Battery ||
    formCategory === TrackerCategory.Sensor ||
    formCategory === TrackerCategory.Cannula}
    <div class="flex justify-center py-2">
      {#if formCategory === TrackerCategory.Battery}
        <Artwork
          icon={batteryArtwork}
          palette="water"
          motion="auto"
          autoplay="once"
          class="size-56"
        />
      {:else}
        <Artwork
          artwork="cgm-sensor"
          palette="water"
          motion="auto"
          autoplay="once"
          class="size-56"
        />
      {/if}
    </div>
  {/if}

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
    <Label>Tracker mode</Label>
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
      <Label for="lifespan">Expected lifespan</Label>
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

  {#if showsCovers}
    <div class="space-y-3" data-testid="tracker-covers">
      <div>
        <Label>Replaced with this part</Label>
        <p class="text-xs text-muted-foreground">
          Select what is new each time this tracker restarts. For a pod, select
          all three.
        </p>
      </div>
      <div class="grid grid-cols-2 gap-2">
        {#each capabilities as capability (capability)}
          <div class="flex items-center gap-2">
            <Checkbox
              id="covers-{capability}"
              checked={formCovers.includes(capability)}
              onCheckedChange={(checked) => toggleCover(capability, checked === true)}
            />
            <Label for="covers-{capability}" variant="option" class="cursor-pointer">
              {capabilityLabels[capability]}
            </Label>
          </div>
        {/each}
      </div>
    </div>
  {/if}
  {#each postedCovers as capability, i (i)}
    <input type="hidden" name="{prefix}covers[]" value={capability} />
  {/each}

  {#if holdsInsulin}
    <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
      <div class="space-y-2">
        <Label for="lowReservoirUnits">Low reservoir alert (units)</Label>
        <Input
          id="lowReservoirUnits"
          type="number"
          min="0.5"
          max="1000"
          step="0.5"
          bind:value={formLowReservoirUnits}
          placeholder="e.g., 20"
        />
        <p class="text-xs text-muted-foreground">
          Alerts when the estimated reservoir level falls below this many
          units. Leave empty for no level alert.
        </p>
      </div>
      <div class="space-y-2">
        <Label for="lowReservoirUrgency">Level alert urgency</Label>
        <Select.Root
          type="single"
          bind:value={formLowReservoirUrgency}
        >
          <Select.Trigger>
            {formLowReservoirUrgency === NotificationUrgency.Info
              ? "Info"
              : formLowReservoirUrgency === NotificationUrgency.Warn
                ? "Warning"
                : formLowReservoirUrgency === NotificationUrgency.Hazard
                  ? "Hazard"
                  : "Urgent"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value={NotificationUrgency.Info} label="Info" />
            <Select.Item value={NotificationUrgency.Warn} label="Warning" />
            <Select.Item value={NotificationUrgency.Hazard} label="Hazard" />
            <Select.Item value={NotificationUrgency.Urgent} label="Urgent" />
          </Select.Content>
        </Select.Root>
      </div>
    </div>
    <input
      type="hidden"
      name="n:{prefix}lowReservoirUnits"
      value={formLowReservoirUnits ?? ""}
    />
    <input
      type="hidden"
      name="{prefix}lowReservoirUrgency"
      value={formLowReservoirUrgency}
    />
  {/if}

  {#if formMode === TrackerMode.Duration}
    <div class="space-y-3" data-testid="tracker-triggers">
      <div>
        <Label>Restart automatically on</Label>
        <p class="text-xs text-muted-foreground">
          When your pump, loop or a connector reports one of these, the running
          tracker is completed and a new one starts at that moment.
        </p>
      </div>
      <div class="grid grid-cols-1 gap-2 @sm:grid-cols-2">
        {#each triggerEventTypes as eventType (eventType)}
          <div class="flex items-center gap-2">
            <Checkbox
              id="trigger-{eventType}"
              checked={formTriggerEventTypes.includes(eventType)}
              onCheckedChange={(checked) => toggleTrigger(eventType, checked === true)}
            />
            <Label for="trigger-{eventType}" variant="option" class="cursor-pointer">
              {eventType}
            </Label>
          </div>
        {/each}
      </div>
    </div>
    <div class="space-y-3" data-testid="tracker-ends">
      <div>
        <Label>Stop automatically on</Label>
        <p class="text-xs text-muted-foreground">
          When your pump, loop or a connector reports one of these, the running
          tracker is completed. A new one does not start until a restart event.
        </p>
      </div>
      <div class="grid grid-cols-2 gap-2">
        {#each endEventTypes as eventType (eventType)}
          <div class="flex items-center gap-2">
            <Checkbox
              id="end-{eventType}"
              checked={formEndEventTypes.includes(eventType)}
              onCheckedChange={(checked) => toggleEnd(eventType, checked === true)}
            />
            <Label for="end-{eventType}" variant="option" class="cursor-pointer">
              {eventType}
            </Label>
          </div>
        {/each}
      </div>
      {#if followsEvents}
        <div class="space-y-2">
          <Label for="triggerNotesContains" size="sm">
            Only when the event's notes contain (optional)
          </Label>
          <Input
            id="triggerNotesContains"
            bind:value={formTriggerNotesContains}
            placeholder="e.g., left"
          />
          <p class="text-xs text-muted-foreground">
            Use this to keep two trackers that listen for the same event apart,
            such as a left and a right site.
          </p>
        </div>
      {/if}
    </div>
  {/if}

  {#each postedTriggers as eventType, i (i)}
    <input type="hidden" name="{prefix}triggerEventTypes[]" value={eventType} />
  {/each}
  {#each postedEnds as eventType, i (i)}
    <input type="hidden" name="{prefix}endEventTypes[]" value={eventType} />
  {/each}
  <input
    type="hidden"
    name="{prefix}triggerNotesContains"
    value={followsEvents ? formTriggerNotesContains : ""}
  />

  <TrackerNotificationEditor
    bind:notifications={formNotifications}
    mode={formMode}
    lifespanHours={formMode === TrackerMode.Duration
      ? formLifespanHours
      : undefined}
  />

  {#each notificationsToApiFormat(formNotifications) as threshold, i (i)}
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
    <Label for="dashboardVisibility">Dashboard visibility</Label>
    <Select.Root
      type="single"
      name="{prefix}dashboardVisibility"
      bind:value={formDashboardVisibility}
    >
      <Select.Trigger>
        {#if formDashboardVisibility === DashboardVisibility.Off}
          Off - Do not show on dashboard
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
          label="Off - Do not show on dashboard"
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

  <div class="space-y-3 pt-2 border-t">
    <Label>Event integration (Nightscout)</Label>
    <p class="text-xs text-muted-foreground -mt-1">
      Optionally create treatment events when this tracker starts or completes.
      This maintains compatibility with existing CAGE/SAGE pills.
    </p>

    <div class="space-y-2">
      <Label for="startEventType" size="sm">Create event on start</Label>
      <EventTypeCombobox
        bind:value={formStartEventType}
        onSelect={(type) => (formStartEventType = type)}
        placeholder="None - do not create event"
      />
    </div>

    <div class="space-y-2">
      <Label for="completionEventType" size="sm">
        Create event on completion
      </Label>
      <EventTypeCombobox
        bind:value={formCompletionEventType}
        onSelect={(type) => (formCompletionEventType = type)}
        placeholder="None - do not create event"
      />
    </div>
  </div>
{/snippet}

<Dialog.Root bind:open>
  <Dialog.Content
    class="@container max-h-[90vh] overflow-y-auto"
    data-testid="tracker-editor"
  >
    <Dialog.Header>
      <Dialog.Title>
        {isNewDefinition ? "New tracker definition" : "Edit definition"}
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