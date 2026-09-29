<script lang="ts">
  import Activity from "@lucide/svelte/icons/activity";
  import Droplet from "@lucide/svelte/icons/droplet";
  import Target from "@lucide/svelte/icons/target";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import { Button } from "$lib/components/ui/button";
  import { FormError } from "$lib/forms";
  import { resolve } from "$app/paths";
  import { describeSubmitError, errorStatus } from "$lib/forms/submit-error";
  import ScheduleView from "$lib/components/schedule/ScheduleView.svelte";
  import { enterTherapySettings } from "$api/generated/setupTherapies.generated.remote";
  import { SetupHubItemKey, TherapyGlucoseField, type WrongUnitRule } from "$api";
  import { setupHubItems } from "$lib/setup-hub/items.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getUnitLabel } from "$lib/utils/formatting";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  interface Props {
    voice: PatientVoice;
    /** The server's wrong-unit checks; only the owner's units are applied. */
    rules: WrongUnitRule[];
    onsaved: () => void | Promise<void>;
    /** A profile arrived while the form was being filled in, so nothing was saved. */
    onconflict: () => void | Promise<void>;
  }

  let { voice, rules, onsaved, onconflict }: Props = $props();

  const devicesHref = resolve("/(unauthenticated)/setup/(guided)/[item]", {
    item: setupHubItems()[SetupHubItemKey.Devices].slug,
  });

  // Every block starts with its time and nothing else: no value is ever suggested.
  let basal = $state<{ time?: string; value?: number }[]>([{ time: "00:00" }]);
  let carbRatio = $state<{ time?: string; value?: number }[]>([{ time: "00:00" }]);
  let sensitivity = $state<{ time?: string; value?: number }[]>([{ time: "00:00" }]);
  let targetRange = $state<{ time?: string; low?: number; high?: number }[]>([
    { time: "00:00" },
  ]);

  let saving = $state(false);
  let error = $state<string | undefined>(undefined);

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));
  const otherUnitLabel = $derived(getUnitLabel(units === "mmol" ? "mg/dl" : "mmol"));
  const glucoseStep = $derived(units === "mmol" ? 0.1 : 1);

  function unitWarning(field: TherapyGlucoseField) {
    return (value: number) =>
      rules.some(
        (r) =>
          r.field === field &&
          r.glucoseUnits === units &&
          ((r.below != null && value < r.below) || (r.above != null && value > r.above))
      )
        ? `This looks like a value in ${otherUnitLabel}, but these fields are in ${unitLabel}. Check which unit the number from the care team is in.`
        : undefined;
  }

  async function save() {
    saving = true;
    error = undefined;
    try {
      await enterTherapySettings({
        glucoseUnits: units,
        basal: basal.map((e) => ({ time: e.time ?? "", value: e.value })),
        carbRatio: carbRatio.map((e) => ({ time: e.time ?? "", value: e.value })),
        sensitivity: sensitivity.map((e) => ({ time: e.time ?? "", value: e.value })),
        targetRange: targetRange.map((e) => ({ time: e.time ?? "", low: e.low, high: e.high })),
      });
      await onsaved();
    } catch (err) {
      if (errorStatus(err) === 409) await onconflict();
      else error = describeSubmitError(err, "We couldn't save these settings.");
    } finally {
      saving = false;
    }
  }
</script>

<div class="flex flex-col gap-6" data-testid="therapy-entry">
  <div class="flex flex-col gap-2 text-sm leading-relaxed">
    {#if voice.kind === "self"}
      <p>
        Nocturne uses your basal rates, carb ratios, insulin sensitivity and target range to
        estimate insulin on board, carbs on board and where your glucose may be heading. Apps
        connected to Nocturne may show those estimates.
      </p>
      <p class="font-medium">
        Enter the values your care team set. Nocturne never suggests or adjusts doses.
      </p>
    {:else if voice.kind === "named"}
      <p>
        Nocturne uses {voice.name}'s basal rates, carb ratios, insulin sensitivity and target
        range to estimate insulin on board, carbs on board and where {voice.name}'s glucose may
        be heading. Apps connected to Nocturne may show those estimates.
      </p>
      <p class="font-medium">
        Enter the values {voice.name}'s care team set. Nocturne never suggests or adjusts doses.
      </p>
    {:else}
      <p>
        Nocturne uses the basal rates, carb ratios, insulin sensitivity and target range to
        estimate insulin on board, carbs on board and where glucose may be heading. Apps
        connected to Nocturne may show those estimates.
      </p>
      <p class="font-medium">
        Enter the values the care team set. Nocturne never suggests or adjusts doses.
      </p>
    {/if}
    <p class="text-muted-foreground">
      This is optional. Glucose values here are in {unitLabel}, the unit chosen during setup.
    </p>
    <p class="text-muted-foreground" data-testid="therapy-defaults">
      {#if voice.kind === "self"}
        Any schedule left out uses Nocturne's built-in default instead. If you use any of these
        settings, enter your insulin sensitivity and target range as well.
      {:else if voice.kind === "named"}
        Any schedule left out uses Nocturne's built-in default instead. If {voice.name} uses any
        of these settings, enter {voice.name}'s insulin sensitivity and target range as well.
      {:else}
        Any schedule left out uses Nocturne's built-in default instead. If any of these settings
        are in use, enter the insulin sensitivity and target range as well.
      {/if}
      How long insulin acts comes from the insulin set in
      <a class="underline underline-offset-2" href={devicesHref}>Devices</a>, or 3 hours if none is
      set.
    </p>
  </div>

  <ScheduleView
    title="Basal rates"
    description="Background insulin per hour."
    unit="U/hr"
    icon={Activity}
    entries={basal}
    onchange={(entries) => (basal = entries)}
    step={0.05}
  />

  <ScheduleView
    title="Carb ratios"
    description="Grams of carbohydrate one unit of insulin covers."
    unit="g/U"
    icon={Droplet}
    iconClass="text-success"
    entries={carbRatio}
    onchange={(entries) => (carbRatio = entries)}
    step={0.5}
  />

  <ScheduleView
    title="Insulin sensitivity"
    description="How far one unit of insulin lowers glucose."
    unit="{unitLabel}/U"
    icon={TrendingUp}
    entries={sensitivity}
    onchange={(entries) => (sensitivity = entries)}
    step={glucoseStep}
    warning={unitWarning(TherapyGlucoseField.Sensitivity)}
  />

  <ScheduleView
    title="Target range"
    description="The glucose range the settings aim for."
    unit={unitLabel}
    icon={Target}
    iconClass="text-glucose-in-range"
    entries={targetRange}
    onchange={(entries) => (targetRange = entries)}
    range
    step={glucoseStep}
    warning={unitWarning(TherapyGlucoseField.Target)}
  />

  <div class="flex flex-wrap items-center justify-end gap-3">
    <FormError issues={error} />
    <Button disabled={saving} onclick={save}>
      {saving ? "Saving..." : "Save these settings"}
    </Button>
  </div>
</div>
