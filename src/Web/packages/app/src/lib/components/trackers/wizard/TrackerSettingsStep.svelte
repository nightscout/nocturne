<script lang="ts">
  import { untrack } from "svelte";
  import { TrackerMode } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { DurationInput } from "#lib/components/ui/duration-input/index.js";
  import { enumValue } from "#lib/components/ui/enum-value.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import { beginLifespanEdit, holdsInsulin, showsCovers, type TrackerDraft } from "../draft";
  import TrackerAutomationFields from "../TrackerAutomationFields.svelte";
  import TrackerCoversField from "../TrackerCoversField.svelte";
  import TrackerEventIntegrationFields from "../TrackerEventIntegrationFields.svelte";
  import TrackerReservoirFields from "../TrackerReservoirFields.svelte";
  import { lifespanText } from "#lib/components/alerts/wizard/templates.svelte.js";
  import { eventList } from "./templates.svelte";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
    /** Whether the person chooses how the tracker counts; a ready-made type has that set. */
    choosesMode: boolean;
    /** The name the field shows as an example. */
    namePlaceholder: string;
  }

  let { draft = $bindable(), choosesMode, namePlaceholder }: Props = $props();

  // Replaced each time the lifespan field takes the focus; see {@link beginLifespanEdit}.
  let setLifespan = untrack(() => beginLifespanEdit(draft));

  const isDuration = $derived(draft.mode === TrackerMode.Duration);

  function changeMode(value: string) {
    const mode = enumValue(TrackerMode, value);
    if (!mode) return;
    draft.mode = mode;
    // A threshold before the end of a lifespan has no meaning for a date, and the reverse.
    draft.notifications = [];
  }
</script>

<div class="space-y-6">
  <div class="space-y-2">
    <Label for="tracker-name">Name</Label>
    <Input
      id="tracker-name"
      class="max-w-sm"
      maxlength={256}
      bind:value={draft.name}
      placeholder={namePlaceholder}
    />
  </div>

  {#if choosesMode}
    <div class="space-y-2">
      <Label id="tracker-mode-label">How it counts</Label>
      <RadioGroup.Root
        class="grid gap-2 @lg:grid-cols-2"
        aria-labelledby="tracker-mode-label"
        value={draft.mode}
        onValueChange={changeMode}
      >
        <RadioGroup.Card value={TrackerMode.Duration} data-testid="tracker-mode-Duration">
          <span class="text-sm font-medium">From a start</span>
          <span class="text-sm text-muted-foreground">
            You start it, and it counts the time since then.
          </span>
        </RadioGroup.Card>
        <RadioGroup.Card value={TrackerMode.Event} data-testid="tracker-mode-Event">
          <span class="text-sm font-medium">To a date</span>
          <span class="text-sm text-muted-foreground">
            You give it a date and a time, and it counts down to them.
          </span>
        </RadioGroup.Card>
      </RadioGroup.Root>
    </div>
  {/if}

  {#if isDuration}
    <div class="space-y-2">
      <Label for="tracker-lifespan">How long it lasts</Label>
      <div class="max-w-56" onfocusin={() => (setLifespan = beginLifespanEdit(draft))}>
        <DurationInput
          id="tracker-lifespan"
          value={draft.lifespanHours}
          onchange={(hours) => setLifespan(hours)}
          placeholder="e.g., 10d"
        />
      </div>
      <p class="text-sm text-muted-foreground">
        {#if draft.lifespanHours}
          <span class="font-medium text-foreground" data-testid="tracker-lifespan-text">
            {lifespanText(draft.lifespanHours)}.
          </span>
        {/if}
        Enter days or hours, for example 10d or 72h. Leave it empty if there is no limit.
      </p>
    </div>
  {:else}
    <p class="text-sm text-muted-foreground">
      You set the date and the time when you start the tracker.
    </p>
  {/if}

  {#if holdsInsulin(draft)}
    <TrackerReservoirFields bind:draft />
  {/if}

  {#if isDuration}
    <p class="text-sm text-muted-foreground" data-testid="tracker-restart-summary">
      {#if draft.triggerEventTypes.length > 0}
        The tracker starts again when your pump, loop or a connector records one of these events:
        {eventList(draft.triggerEventTypes)}.
      {:else}
        You start the tracker again yourself each time.
      {/if}
    </p>
  {/if}

  <Collapsible.Root>
    <Collapsible.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button
          {...props}
          variant="subtle"
          size="touch-sm"
          class="group"
          data-testid="tracker-more-settings"
        >
          <ChevronDown
            class="transition-transform duration-200 group-data-[state=open]:rotate-180"
            aria-hidden="true"
          />
          More settings
        </Button>
      {/snippet}
    </Collapsible.Trigger>
    <Collapsible.Content>
      <div class="mt-4 space-y-6">
        <div class="space-y-2">
          <Label for="tracker-description">Description (optional)</Label>
          <Input id="tracker-description" bind:value={draft.description} />
        </div>
        {#if showsCovers(draft)}
          <TrackerCoversField bind:draft />
        {/if}
        {#if isDuration}
          <TrackerAutomationFields bind:draft />
        {/if}
        <TrackerEventIntegrationFields bind:draft />
      </div>
    </Collapsible.Content>
  </Collapsible.Root>
</div>
