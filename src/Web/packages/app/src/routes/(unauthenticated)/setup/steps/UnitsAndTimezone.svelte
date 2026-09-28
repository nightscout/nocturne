<script lang="ts">
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import Info from "@lucide/svelte/icons/info";
  import * as Alert from "$lib/components/ui/alert";
  import * as RadioGroup from "$lib/components/ui/radio-group";
  import TimezoneCombobox from "$lib/components/patient/TimezoneCombobox.svelte";
  import { FormError, FormField } from "$lib/forms";
  import {
    formatGlucoseRange,
    formatGlucoseValue,
    getUnitLabel,
    isGlucoseUnits,
    type GlucoseUnits,
  } from "$lib/utils/formatting";
  import type { NightscoutDisplaySettings } from "$api";

  interface Props {
    units: GlucoseUnits | undefined;
    timezone: string;
    /** True while `timezone` is this device's own zone rather than a stored or imported one. */
    timezoneDetected: boolean;
    nightscout?: NightscoutDisplaySettings | null;
    nightscoutUnavailable?: boolean;
    /** Who the copy speaks of: the patient themself, a named patient, or neither. */
    voice: { self: boolean; named: boolean; name?: string };
    error?: string;
  }

  let {
    units = $bindable(),
    timezone = $bindable(),
    timezoneDetected,
    nightscout,
    nightscoutUnavailable = false,
    voice,
    error,
  }: Props = $props();

  // Illustrative values in mg/dL, shown in each unit through the app's own formatter.
  const EXAMPLE_READING_MGDL = 110;
  const EXAMPLE_RANGE_MGDL = [70, 180] as const;

  const OPTIONS: { value: GlucoseUnits; name: string }[] = [
    { value: "mg/dl", name: "Milligrams per decilitre" },
    { value: "mmol", name: "Millimoles per litre" },
  ];

  const profileUnits = $derived(
    isGlucoseUnits(nightscout?.profileUnits) ? nightscout.profileUnits : undefined
  );
  const profileDiffers = $derived(
    !!units && !!profileUnits && profileUnits !== units
  );
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <div class="flex flex-col items-center gap-4 text-center">
    <!-- prettier-ignore -->
    <h1
      id="units-heading"
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl md:text-4xl xl:text-5xl"
    >
      Glucose units and <em class="not-italic font-light text-primary">time</em>.
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
      {#if voice.self}
        Pick the unit your meter or sensor app shows.
      {:else if voice.named}
        Pick the unit {voice.name}'s meter or sensor app shows.
      {:else}
        Pick the unit the meter or sensor app shows.
      {/if}
      The same reading looks very different in each, so check the example matches
      what you're used to.
    </p>
  </div>

  {#if nightscout}
    <Alert.Root variant="info" class="max-w-200">
      <Info />
      <Alert.Description>
        We've filled these in from your Nightscout. Check they're right.
      </Alert.Description>
    </Alert.Root>
  {:else if nightscoutUnavailable}
    <Alert.Root variant="warning" class="max-w-200">
      <TriangleAlert />
      <Alert.Description>
        We couldn't read your Nightscout's settings, so check these match what it
        uses.
      </Alert.Description>
    </Alert.Root>
  {/if}

  <RadioGroup.Root
    class="w-full max-w-200 grid-cols-1 gap-5 sm:grid-cols-2"
    aria-labelledby="units-heading"
    value={units}
    onValueChange={(value) => {
      const option = OPTIONS.find((o) => o.value === value);
      if (option) units = option.value;
    }}
  >
    {#each OPTIONS as option (option.value)}
      {@const label = getUnitLabel(option.value)}
      <RadioGroup.Card value={option.value} aria-label={`${label}, ${option.name}`}>
        {#snippet children({ checked })}
          <span class="flex w-full flex-col gap-4 p-2">
            <span
              class="absolute right-4 top-4 block h-5 w-5 rounded-full border-2 transition-colors {checked
                ? 'border-primary bg-primary inset-ring-4 inset-ring-card'
                : 'border-border'}"
            ></span>

            <span class="flex flex-col gap-1">
              <span class="font-brand text-xl font-normal leading-snug text-foreground">
                {label}
              </span>
              <span class="text-sm text-muted-foreground">{option.name}</span>
            </span>

            <span class="flex flex-col gap-1" data-testid="units-example">
              <span class="text-xs uppercase tracking-wide text-muted-foreground">
                Example reading
              </span>
              <span class="font-mono text-3xl text-foreground">
                {formatGlucoseValue(EXAMPLE_READING_MGDL, option.value)}
                <span class="text-base text-muted-foreground">{label}</span>
              </span>
              <span class="text-sm text-muted-foreground">
                Example range: {formatGlucoseRange(
                  EXAMPLE_RANGE_MGDL[0],
                  EXAMPLE_RANGE_MGDL[1],
                  option.value
                )}
              </span>
            </span>
          </span>
        {/snippet}
      </RadioGroup.Card>
    {/each}
  </RadioGroup.Root>

  {#if profileDiffers && profileUnits && units}
    <Alert.Root variant="warning" class="max-w-200" data-testid="profile-units-mismatch">
      <TriangleAlert />
      <Alert.Title>Your Nightscout profile uses {getUnitLabel(profileUnits)}</Alert.Title>
      <Alert.Description>
        Its targets and insulin sensitivity were entered in {getUnitLabel(profileUnits)},
        but you've chosen {getUnitLabel(units)}. Nocturne will show them in
        {getUnitLabel(units)}, so the numbers will look different from Nightscout.
        Nothing in Nightscout, the pump or the loop app is changed.
      </Alert.Description>
    </Alert.Root>
  {/if}

  <div class="w-full max-w-md">
    <FormField label="Timezone" id="setup-timezone" labelVariant="muted">
      {#snippet control(field)}
        <TimezoneCombobox {...field} bind:value={timezone} placeholder="Search timezones..." />
      {/snippet}
      {#snippet hint()}
        <p class="text-xs text-muted-foreground">
          {#if timezoneDetected}
            Detected from this device. Change it if you usually live somewhere else.
          {:else if voice.self}
            Times of day, like overnight alerts and daily reports, follow your timezone.
          {:else if voice.named}
            Times of day, like overnight alerts and daily reports, follow {voice.name}'s
            timezone.
          {:else}
            Times of day, like overnight alerts and daily reports, follow this timezone.
          {/if}
        </p>
      {/snippet}
    </FormField>
  </div>

  <FormError issues={error} />
</div>
