<script lang="ts">
  import { untrack } from "svelte";
  import { labelFor } from "$lib/components/ui/enum-value";
  import Activity from "@lucide/svelte/icons/activity";
  import Cpu from "@lucide/svelte/icons/cpu";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Timer from "@lucide/svelte/icons/timer";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { Switch } from "$lib/components/ui/switch";
  import { Toggle } from "$lib/components/ui/toggle";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { PatientDeviceManager, PatientInsulinManager, aidAlgorithmLabels } from "$lib/components/patient";
  import {
    getDeviceSetup,
    confirmSetupDevice,
    addSetupInsulin,
    setTakesNoInsulin,
    addSetupTracker,
  } from "$api/generated/setupDevices.generated.remote";
  import { getDevices, getInsulins, deleteInsulin } from "$api/generated/patientRecords.generated.remote";
  import { deleteDefinition } from "$api/generated/trackers.generated.remote";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import {
    DeviceCategory,
    DeviceEvidenceSource,
    InsulinGroup,
    TrackerOfferKind,
    type DeviceEvidence,
    type DeviceSlot,
    type InsulinFormulation,
    type TrackerOffer,
  } from "$api";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";

  const setupQuery = getDeviceSetup();
  const hubQuery = getSetupHub();
  const devicesQuery = getDevices();
  const insulinsQuery = getInsulins();
  const relationshipQuery = getPatientRelationship();
  const voice = $derived(patientVoice(relationshipQuery.current));
  const setup = $derived(setupQuery.current);

  // The device and insulin managers below write through their own remote functions, which
  // refresh their lists but not this page's guesses or the hub's done state.
  $effect(() => {
    void devicesQuery.current;
    void insulinsQuery.current;
    untrack(() => {
      void setupQuery.refresh();
      void hubQuery.refresh();
    });
  });

  let busy = $state(false);
  let error = $state<string | undefined>(undefined);
  let chosen = $state<Partial<Record<DeviceCategory, string>>>({});

  async function run(write: () => Promise<unknown>) {
    busy = true;
    error = undefined;
    try {
      await write();
      await hubQuery.refresh();
    } catch (err) {
      error = describeSubmitError(err, "We couldn't save that. Please try again.");
    } finally {
      busy = false;
    }
  }

  const openSlots = $derived((setup?.devices ?? []).filter((s) => !s.recorded));
  const anyGuess = $derived(openSlots.some((s) => s.guess));

  function selectedId(slot: DeviceSlot): string {
    return chosen[slot.category!] ?? slot.guess?.id ?? "";
  }

  function confirm(slot: DeviceSlot) {
    const catalogId = selectedId(slot);
    if (!catalogId) return;
    return run(async () => {
      await confirmSetupDevice({
        catalogId,
        aidAlgorithm:
          slot.category === DeviceCategory.InsulinPump ? setup?.algorithm?.algorithm : undefined,
      });
      await devicesQuery.refresh();
    });
  }

  const evidenceLabels: Record<DeviceEvidenceSource, string> = {
    [DeviceEvidenceSource.Connector]: "Connected account",
    [DeviceEvidenceSource.PumpStatus]: "Pump status",
    [DeviceEvidenceSource.AlgorithmStatus]: "App status",
    [DeviceEvidenceSource.Readings]: "Reading label",
  };

  function evidenceText(e: DeviceEvidence): string {
    const detail =
      e.source === DeviceEvidenceSource.AlgorithmStatus
        ? (labelFor(aidAlgorithmLabels, e.detail) ?? e.detail)
        : e.detail;
    return `${evidenceLabels[e.source!]}: ${detail}`;
  }

  const insulinIdOn = (formulation: InsulinFormulation) =>
    setup?.insulins?.find((i) => i.formulationId === formulation.id)?.id;

  const otherInsulins = $derived.by(() => {
    const listed = new Set(
      (setup?.insulinChoices ?? []).flatMap((g) => (g.formulations ?? []).map((f) => f.id))
    );
    return (setup?.insulins ?? []).filter((i) => !listed.has(i.formulationId ?? undefined));
  });

  function toggleInsulin(formulation: InsulinFormulation) {
    const recordedId = insulinIdOn(formulation);
    return run(async () => {
      if (recordedId) await deleteInsulin(recordedId);
      else await addSetupInsulin({ formulationId: formulation.id! });
      await Promise.all([setupQuery.refresh(), insulinsQuery.refresh()]);
    });
  }

  function answerNone(takesNoInsulin: boolean) {
    return run(() => setTakesNoInsulin({ takesNoInsulin }));
  }

  const trackerTitles: Record<TrackerOfferKind, string> = {
    [TrackerOfferKind.Sensor]: "Sensor changes",
    [TrackerOfferKind.Pod]: "Pod changes",
    [TrackerOfferKind.InfusionSet]: "Infusion set changes",
    [TrackerOfferKind.Reservoir]: "Reservoir changes",
  };

  function trackerName(offer: TrackerOffer): string {
    switch (offer.kind) {
      case TrackerOfferKind.Sensor:
        return `${offer.deviceName} sensor`;
      case TrackerOfferKind.Pod:
        return `${offer.deviceName} pod`;
      case TrackerOfferKind.InfusionSet:
        return `${offer.deviceName} infusion set`;
      default:
        return `${offer.deviceName} reservoir`;
    }
  }

  function wearTime(hours: number): string {
    return hours % 24 === 0 ? `${hours / 24} days` : `${hours} hours`;
  }

  function toggleTracker(offer: TrackerOffer, on: boolean) {
    return run(async () => {
      if (on) await addSetupTracker({ kind: offer.kind!, name: trackerName(offer) });
      else if (offer.definitionId) {
        await deleteDefinition(offer.definitionId);
        await setupQuery.refresh();
      }
    });
  }
</script>

{#if !setup}
  <p class="text-sm text-muted-foreground">Looking at what's connected...</p>
{:else}
  <div class="flex flex-col gap-8" data-testid="device-setup">
    <section class="flex flex-col gap-4" aria-labelledby="devices-heading">
      <div class="flex flex-col gap-1">
        <h2 id="devices-heading" class="text-lg font-medium">Sensor and pump</h2>
        {#if openSlots.length > 0}
          <p class="text-sm text-muted-foreground">
            {#if anyGuess}
              {#if voice.kind === "self"}
                We looked at what's connected and think you use these. Is that right?
              {:else if voice.kind === "named"}
                We looked at what's connected and think {voice.name} uses these. Is that right?
              {:else}
                We looked at what's connected and think these are in use. Is that right?
              {/if}
            {:else if voice.kind === "self"}
              Pick the sensor and pump you use.
            {:else if voice.kind === "named"}
              Pick the sensor and pump {voice.name} uses.
            {:else}
              Pick the sensor and pump in use.
            {/if}
          </p>
        {/if}
      </div>

      {#each openSlots as slot (slot.category)}
        {@const isPump = slot.category === DeviceCategory.InsulinPump}
        {@const Icon = isPump ? Cpu : Activity}
        <div
          class="flex flex-col gap-3 rounded-lg border p-4"
          data-testid="device-slot-{isPump ? 'pump' : 'cgm'}"
        >
          <div class="flex items-start gap-3">
            <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-muted">
              <Icon class="h-4 w-4" />
            </span>
            <div class="flex min-w-0 flex-1 flex-col gap-1">
              <span class="text-xs text-muted-foreground">
                {isPump ? "Insulin pump" : "Sensor (CGM, continuous glucose monitor)"}
              </span>
              {#if slot.guess}
                <span class="font-medium">
                  {#if isPump && setup.algorithm}
                    {slot.guess.name} with {aidAlgorithmLabels[setup.algorithm.algorithm!]}
                  {:else}
                    {slot.guess.name}
                  {/if}
                </span>
                {#if !slot.modelKnown}
                  <span class="text-xs text-muted-foreground">
                    We could only tell the brand, so please check the model.
                  </span>
                {/if}
                <div class="flex flex-wrap items-center gap-1.5">
                  <span class="text-xs text-muted-foreground">Seen in</span>
                  {#each [...(slot.evidence ?? []), ...(isPump ? (setup.algorithm?.evidence ?? []) : [])] as e, i (i)}
                    <Badge variant="outline">{evidenceText(e)}</Badge>
                  {/each}
                </div>
              {:else}
                <span class="text-sm text-muted-foreground">
                  {isPump
                    ? "Nothing connected names a pump. Pick one, or leave this if no pump is used."
                    : "Nothing connected names a sensor yet. Pick one from the list."}
                </span>
              {/if}
            </div>
          </div>

          <div class="flex flex-wrap items-end gap-2">
            <div class="flex min-w-48 flex-1 flex-col gap-1.5">
              <Label for="model-{slot.category}">{slot.guess ? "Or pick another model" : "Model"}</Label>
              <Select.Root
                type="single"
                value={selectedId(slot)}
                onValueChange={(v) => (chosen[slot.category!] = v)}
              >
                <Select.Trigger id="model-{slot.category}">
                  {slot.choices?.find((c) => c.id === selectedId(slot))?.name ?? "Choose a model"}
                </Select.Trigger>
                <Select.Content>
                  {#each slot.choices ?? [] as choice (choice.id)}
                    <Select.Item value={choice.id!} label={choice.name} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
            <Button disabled={busy || !selectedId(slot)} onclick={() => confirm(slot)}>
              {slot.guess && selectedId(slot) === slot.guess.id ? "That's right" : "Use this one"}
            </Button>
          </div>
        </div>
      {/each}

      <div class="flex flex-col gap-3">
        <h3 class="text-sm font-medium">On record</h3>
        <PatientDeviceManager variant="inline" />
      </div>
    </section>

    <section class="flex flex-col gap-4" aria-labelledby="insulin-heading">
      <div class="flex flex-col gap-1">
        <h2 id="insulin-heading" class="flex items-center gap-2 text-lg font-medium">
          <Syringe class="h-4 w-4" />
          Insulin
        </h2>
        <p class="text-sm text-muted-foreground">
          {#if voice.kind === "self"}
            Which insulins do you use? Pick every one that applies.
          {:else if voice.kind === "named"}
            Which insulins does {voice.name} use? Pick every one that applies.
          {:else}
            Which insulins are used? Pick every one that applies.
          {/if}
        </p>
      </div>

      {#each setup.insulinChoices ?? [] as group (group.group)}
        <div class="flex flex-col gap-2" data-testid="insulin-group-{group.group}">
          <h3 class="text-sm font-medium">
            {group.group === InsulinGroup.RapidActing
              ? "Rapid-acting (mealtime and pump insulin)"
              : "Long-acting (background insulin taken once or twice a day)"}
          </h3>
          <div class="flex flex-wrap gap-2">
            {#each group.formulations ?? [] as formulation (formulation.id)}
              <Toggle
                variant="outline"
                size="sm"
                pressed={!!insulinIdOn(formulation)}
                disabled={busy}
                onPressedChange={() => toggleInsulin(formulation)}
              >
                {formulation.name}
              </Toggle>
            {/each}
          </div>
        </div>
      {/each}

      {#if otherInsulins.length > 0}
        <p class="text-sm text-muted-foreground" data-testid="other-insulins">
          Also on record: {otherInsulins.map((i) => i.name).join(", ")}
        </p>
      {/if}

      <div class="flex items-center gap-2">
        <Switch
          id="takes-no-insulin"
          checked={!!setup.takesNoInsulin}
          disabled={busy || (setup.insulins?.length ?? 0) > 0}
          onCheckedChange={(on) => answerNone(on)}
        />
        <Label for="takes-no-insulin">
          {#if voice.kind === "self"}
            I don't take insulin
          {:else if voice.kind === "named"}
            {voice.name} doesn't take insulin
          {:else}
            No insulin is taken
          {/if}
        </Label>
      </div>

      <p class="text-xs text-muted-foreground">
        Each insulin is recorded with the typical action time from its label, which Nocturne uses to
        estimate insulin on board. This does not change any doses. Check these settings with your
        care team, and change them in patient settings if yours differ.
      </p>

      <Collapsible.Root>
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="link" size="sm">Using an insulin not listed here?</Button>
          {/snippet}
        </Collapsible.Trigger>
        <Collapsible.Content class="flex flex-col gap-3 pt-2">
          <PatientInsulinManager variant="inline" />
        </Collapsible.Content>
      </Collapsible.Root>
    </section>

    {#if (setup.trackers?.length ?? 0) > 0}
      <section class="flex flex-col gap-4" aria-labelledby="trackers-heading">
        <div class="flex flex-col gap-1">
          <h2 id="trackers-heading" class="flex items-center gap-2 text-lg font-medium">
            <Timer class="h-4 w-4" />
            Trackers
          </h2>
          <p class="text-sm text-muted-foreground">
            A tracker counts how long a sensor or pod has been in, so it is easy to see when the next
            change is due. It restarts when a change is logged. These are optional.
          </p>
        </div>
        {#each setup.trackers ?? [] as offer (offer.kind)}
          <div class="flex items-center justify-between gap-4 rounded-lg border p-3" data-testid="tracker-{offer.kind}">
            <div class="flex flex-col gap-0.5">
              <Label for="tracker-{offer.kind}">{trackerTitles[offer.kind!]}</Label>
              <span class="text-xs text-muted-foreground">
                {#if offer.lifespanHours}
                  {offer.deviceName}: rated for {wearTime(offer.lifespanHours)}
                {:else}
                  {offer.deviceName}: no rated wear time, so it counts the days since the last change
                {/if}
              </span>
            </div>
            <Switch
              id="tracker-{offer.kind}"
              checked={!!offer.definitionId}
              disabled={busy}
              onCheckedChange={(on) => toggleTracker(offer, on)}
            />
          </div>
        {/each}
      </section>
    {/if}

    <FormError issues={error} />

    <p class="text-xs text-muted-foreground">
      This is a record of equipment, not medical advice. Follow the instructions that came with each
      device and your care team's advice.
    </p>
  </div>
{/if}
