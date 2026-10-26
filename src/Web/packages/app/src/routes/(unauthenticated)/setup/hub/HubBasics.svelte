<script lang="ts">
  import { browser } from "$app/env";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import {
    getPatientRelationship,
    setPatientRelationship,
    getUnitsAndTimezone,
    setUnitsAndTimezone,
  } from "#lib/api/generated/tenantSettings.generated.remote.js";
  import { PatientRelationship } from "#lib/api/index.js";
  import { applyPreferences } from "#lib/stores/appearance-store.svelte.js";
  import { getUnitLabel, isGlucoseUnits, type GlucoseUnits } from "#lib/utils/formatting.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import type { PatientVoice } from "#lib/onboarding/patient-voice.svelte.js";
  import WhoFor from "../steps/WhoFor.svelte";
  import UnitsAndTimezone from "../steps/UnitsAndTimezone.svelte";

  let { voice }: { voice: PatientVoice } = $props();

  const relationshipQuery = getPatientRelationship();
  // No locale: Basics shows the units in effect, not the core's suggestion for a first answer.
  const unitsQuery = getUnitsAndTimezone({});

  const stored = $derived(relationshipQuery.current);
  const storedUnits = $derived(
    isGlucoseUnits(unitsQuery.current?.glucoseUnits) ? unitsQuery.current.glucoseUnits : undefined
  );
  const storedTimezone = $derived(unitsQuery.current?.timezone ?? undefined);

  let editing = $state<"who" | "units" | null>(null);
  let error = $state<string | undefined>(undefined);

  let relationship = $state<PatientRelationship | undefined>(undefined);
  let patientName = $state("");
  let units = $state<GlucoseUnits | undefined>(undefined);
  let timezone = $state("");

  function edit(which: "who" | "units") {
    error = undefined;
    relationship = stored?.relationship;
    patientName = stored?.patientName ?? "";
    units = storedUnits;
    timezone = storedTimezone ?? (browser ? Intl.DateTimeFormat().resolvedOptions().timeZone : "");
    editing = which;
  }

  async function save() {
    try {
      error = undefined;
      if (editing === "who" && relationship) {
        await setPatientRelationship({
          relationship,
          patientName: patientName.trim() || undefined,
        });
      } else if (editing === "units" && units && timezone) {
        await setUnitsAndTimezone({ glucoseUnits: units, timezone });
        applyPreferences({ glucoseUnits: units }, { refreshCookie: true });
      }
      editing = null;
    } catch (err) {
      error = describeSubmitError(err, "We could not save that.");
    }
  }

  const canSave = $derived(editing === "who" ? !!relationship : !!units && !!timezone);
</script>

<Card.Root size="flush">
  <Card.Header class="border-b py-5">
    <Card.Title>Basics</Card.Title>
    <Card.Description>What you told us at the start. You can change these any time.</Card.Description>
  </Card.Header>

  <dl class="divide-y">
    <div class="grid grid-cols-[1fr_auto] items-center gap-4 px-6 py-4">
      <div class="flex flex-col gap-1">
        <dt class="text-xs text-muted-foreground">Who it is for</dt>
        <dd class="text-sm font-medium" data-testid="basics-who">
          {#if stored?.relationship === PatientRelationship.Self}
            You
          {:else if stored?.relationship === PatientRelationship.Caregiver && voice.kind === "named"}
            {voice.name}, someone you care for
          {:else if stored?.relationship === PatientRelationship.Caregiver}
            Someone you care for
          {:else if stored?.relationship === PatientRelationship.Helper && voice.kind === "named"}
            {voice.name}, who you are setting it up for
          {:else if stored?.relationship === PatientRelationship.Helper}
            Someone you are setting it up for
          {:else}
            Not answered yet
          {/if}
        </dd>
      </div>
      <Button variant="outline" size="sm" onclick={() => edit("who")}>Change</Button>
    </div>

    <div class="grid grid-cols-[1fr_auto] items-center gap-4 px-6 py-4">
      <div class="flex flex-col gap-1">
        <dt class="text-xs text-muted-foreground">Glucose units and timezone</dt>
        <dd class="text-sm font-medium" data-testid="basics-units">
          {storedUnits ? getUnitLabel(storedUnits) : "Not chosen yet"}
          <span class="text-muted-foreground" aria-hidden="true">&middot;</span>
          {storedTimezone ?? "No timezone yet"}
        </dd>
        <dd class="text-xs text-muted-foreground">
          Therapy settings, like targets and insulin sensitivity, are entered in this unit.
        </dd>
      </div>
      <Button variant="outline" size="sm" onclick={() => edit("units")}>Change</Button>
    </div>
  </dl>
</Card.Root>

<Dialog.Root open={editing !== null} onOpenChange={(open) => !open && (editing = null)}>
  <Dialog.Content class="max-h-[90vh] overflow-y-auto sm:max-w-4xl">
    <Dialog.Header class="sr-only">
      <Dialog.Title>
        {editing === "who" ? "Who it is for" : "Glucose units and timezone"}
      </Dialog.Title>
    </Dialog.Header>

    {#if editing === "who"}
      <WhoFor bind:relationship bind:patientName {error} />
    {:else if editing === "units"}
      <UnitsAndTimezone
        bind:units
        bind:timezone
        timezoneDetected={!storedTimezone}
        {voice}
        {error}
      />
      <p class="px-4 text-center text-sm text-muted-foreground">
        Changing units changes how readings are shown to you and to anyone who has not picked
        their own. Therapy settings, like targets and insulin sensitivity, are entered in the
        unit chosen here. Settings already saved keep the unit they were entered in.
      </p>
    {/if}

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (editing = null)}>Cancel</Button>
      <Button onclick={save} disabled={!canSave}>Save</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
