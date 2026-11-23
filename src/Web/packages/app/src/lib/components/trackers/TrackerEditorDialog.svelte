<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { DurationInput } from "#lib/components/ui/duration-input/index.js";
  import { TrackerNotificationEditor } from "#lib/components/trackers/index.js";
  import { Artwork } from "water-ui";
  import { batteryArtwork } from "#lib/watercolour-icons.js";
  import * as trackersRemote from "#lib/api/generated/trackers.generated.remote.js";
  import { FormError, useSubmission } from "#lib/forms/index.js";
  import { tick, untrack } from "svelte";
  import {
    PumpCapability,
    TrackerCategory,
    TrackerMode,
    type TrackerDefinitionDto,
  } from "#lib/api/index.js";
  import { beginLifespanEdit, holdsInsulin, showsCovers, type TrackerDraft } from "./draft";
  import TrackerAutomationFields from "./TrackerAutomationFields.svelte";
  import TrackerCoversField from "./TrackerCoversField.svelte";
  import TrackerDefinitionInputs from "./TrackerDefinitionInputs.svelte";
  import TrackerEventIntegrationFields from "./TrackerEventIntegrationFields.svelte";
  import TrackerReservoirFields from "./TrackerReservoirFields.svelte";
  import TrackerVisibilityField from "./TrackerVisibilityField.svelte";

  interface Props {
    open?: boolean;
    /** The definition under edit. */
    definition: TrackerDefinitionDto | null;
    /** The definition as the form edits it; the opener sets it from {@link definition}. */
    draft: TrackerDraft;
    categoryLabels: Record<TrackerCategory, string>;
  }

  let { open = $bindable(false), definition, draft = $bindable(), categoryLabels }: Props = $props();

  // Replaced each time the lifespan field takes the focus; see {@link beginLifespanEdit}.
  let setLifespan = untrack(() => beginLifespanEdit(draft));

  // A pod is the site, the insulin and the battery together.
  function changeCategory(category: string) {
    if (category === TrackerCategory.Pod) draft.covers = Object.values(PumpCapability);
  }

  const form = $derived(trackersRemote.updateDefinition.for(definition?.id ?? ""));

  /** Why the definition cannot be saved yet, or `null` when it can. */
  const blocked = $derived(draft.name.trim() === "" ? "Enter a name." : null);

  const submission = useSubmission();
  // A keyed form keeps its issues across openings, so show them only once this opening submits.
  let submitted = $state(false);

  $effect(() => {
    if (open) {
      submission.clear();
      submitted = false;
    }
  });

  async function save(submit: () => Promise<boolean>) {
    submitted = true;
    await submission.run(submit, async () => {
      await tick();
      open = false;
    });
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content
    class="@container max-h-[90vh] overflow-y-auto"
    data-testid="tracker-editor"
  >
    <Dialog.Header>
      <Dialog.Title>Edit definition</Dialog.Title>
    </Dialog.Header>

    <form {...form.enhance(({ submit }) => save(submit))}>
      <input {...form.fields.id.as("hidden", definition?.id ?? "")} />
      <TrackerDefinitionInputs fields={form.fields.request} {draft} />

      <div class="space-y-6 py-4">
        <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
          <div class="space-y-2">
            <Label for="name">Name</Label>
            <Input id="name" bind:value={draft.name} placeholder="e.g., G7 Sensor" />
          </div>
          <div class="space-y-2">
            <Label for="category">Category</Label>
            <Select.Root type="single" bind:value={draft.category} onValueChange={changeCategory}>
              <Select.Trigger id="category" data-testid="tracker-category-trigger">
                {categoryLabels[draft.category]}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value={TrackerCategory.Sensor} label="Sensor" />
                <Select.Item value={TrackerCategory.Cannula} label="Cannula" />
                <Select.Item value={TrackerCategory.Pod} label="Pod" />
                <Select.Item value={TrackerCategory.Battery} label="Battery" />
                <Select.Item value={TrackerCategory.Reservoir} label="Reservoir" />
                <Select.Item value={TrackerCategory.Appointment} label="Appointment" />
                <Select.Item value={TrackerCategory.Reminder} label="Reminder" />
                <Select.Item value={TrackerCategory.Consumable} label="Consumable" />
                <Select.Item value={TrackerCategory.Custom} label="Custom" />
              </Select.Content>
            </Select.Root>
          </div>
        </div>

        {#if draft.category === TrackerCategory.Battery ||
          draft.category === TrackerCategory.Sensor ||
          draft.category === TrackerCategory.Cannula}
          <div class="flex justify-center py-2">
            {#if draft.category === TrackerCategory.Battery}
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
            bind:value={draft.description}
            placeholder="Optional description"
          />
        </div>

        <div class="space-y-2">
          <Label for="mode">Tracker mode</Label>
          <Select.Root type="single" bind:value={draft.mode}>
            <Select.Trigger id="mode">
              {#if draft.mode === TrackerMode.Duration}
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
            {#if draft.mode === TrackerMode.Duration}
              Duration trackers run from a start time for a specified lifespan.
            {:else}
              Event trackers are scheduled for a specific date and time.
            {/if}
          </p>
        </div>

        {#if draft.mode === TrackerMode.Duration}
          <div class="space-y-2">
            <Label for="lifespan">Expected lifespan</Label>
            <div onfocusin={() => (setLifespan = beginLifespanEdit(draft))}>
              <DurationInput
                id="lifespan"
                value={draft.lifespanHours}
                onchange={(hours) => setLifespan(hours)}
                placeholder="e.g., 10x24 or 10d"
              />
            </div>
          </div>
        {/if}

        {#if showsCovers(draft)}
          <TrackerCoversField bind:draft />
        {/if}

        {#if holdsInsulin(draft)}
          <TrackerReservoirFields bind:draft />
        {/if}

        {#if draft.mode === TrackerMode.Duration}
          <TrackerAutomationFields bind:draft />
        {/if}

        <TrackerNotificationEditor
          bind:notifications={draft.notifications}
          mode={draft.mode}
          lifespanHours={draft.mode === TrackerMode.Duration ? draft.lifespanHours : undefined}
        />

        <TrackerVisibilityField bind:draft />

        <div class="pt-2 border-t">
          <TrackerEventIntegrationFields bind:draft />
        </div>
      </div>

      <FormError
        issues={submission.error ?? (submitted ? form.fields.allIssues() : undefined)}
        focusOnShow
        class="mb-4"
      />

      <Dialog.Footer class="sm:items-center">
        {#if blocked}
          <p class="text-sm text-muted-foreground max-sm:order-last sm:mr-auto" role="status">{blocked}</p>
        {/if}
        <Button type="button" variant="outline" onclick={() => (open = false)}>
          Cancel
        </Button>
        <Button type="submit" disabled={blocked !== null || !!form.pending}>
          {form.pending ? "Saving..." : "Save"}
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
