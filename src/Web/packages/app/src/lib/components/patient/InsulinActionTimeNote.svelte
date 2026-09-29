<script lang="ts">
  import { resolve } from "$app/paths";
  import { InsulinActionTimeSource, type InsulinActionTime } from "$api";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  interface Props {
    /** From the server's resolver, so the note names the value insulin on board really uses. */
    actionTime: InsulinActionTime;
    voice: PatientVoice;
  }

  let { actionTime, voice }: Props = $props();

  const hours = $derived(actionTime.hours);
  const insulin = $derived(actionTime.primaryInsulinName);
  const patientSettingsHref = resolve("/(authenticated)/settings/patient");
</script>

<p class="text-xs text-muted-foreground" data-testid="action-time" data-source={actionTime.source}>
  {#if actionTime.source === InsulinActionTimeSource.PrimaryInsulin}
    {#if voice.kind === "self"}
      Nocturne uses the action time of {insulin}, {hours} hours, for insulin on board and predictions,
      in place of the value in your profile. It doesn't change your pump or AID app. You can change the
      time in <a class="underline" href={patientSettingsHref}>patient settings</a>. Check it with your care team.
    {:else if voice.kind === "named"}
      Nocturne uses the action time of {insulin}, {hours} hours, for insulin on board and predictions,
      in place of the value in {voice.name}'s profile. It doesn't change {voice.name}'s pump or AID app.
      You can change the time in <a class="underline" href={patientSettingsHref}>patient settings</a>. Check
      it with the care team.
    {:else}
      Nocturne uses the action time of {insulin}, {hours} hours, for insulin on board and predictions,
      in place of the profile's value. It doesn't change the pump or AID app. You can change the time in
      <a class="underline" href={patientSettingsHref}>patient settings</a>. Check it with the care team.
    {/if}
  {:else if actionTime.source === InsulinActionTimeSource.ExternalProfile}
    {#if voice.kind === "self"}
      Your profile is managed by another app, so Nocturne uses its action time, {hours} hours, for
      insulin on board and predictions.
    {:else if voice.kind === "named"}
      {voice.name}'s profile is managed by another app, so Nocturne uses its action time, {hours} hours,
      for insulin on board and predictions.
    {:else}
      The profile is managed by another app, so Nocturne uses its action time, {hours} hours, for insulin
      on board and predictions.
    {/if}
    {#if insulin}
      That value is used instead of the action time of {insulin}.
    {/if}
  {:else if actionTime.source === InsulinActionTimeSource.Profile}
    {#if voice.kind === "self"}
      Nocturne uses the action time in your profile, {hours} hours, for insulin on board and predictions.
      Once a rapid-acting insulin is set in Devices, its action time is used instead.
    {:else if voice.kind === "named"}
      Nocturne uses the action time in {voice.name}'s profile, {hours} hours, for insulin on board and
      predictions. Once a rapid-acting insulin is set in Devices, its action time is used instead.
    {:else}
      Nocturne uses the action time in the profile, {hours} hours, for insulin on board and predictions.
      Once a rapid-acting insulin is set in Devices, its action time is used instead.
    {/if}
  {:else if insulin}
    {#if voice.kind === "self"}
      Until your therapy settings are set up, Nocturne uses the default of {hours} hours for insulin on
      board and predictions, not the action time of {insulin}.
    {:else if voice.kind === "named"}
      Until {voice.name}'s therapy settings are set up, Nocturne uses the default of {hours} hours for
      insulin on board and predictions, not the action time of {insulin}.
    {:else}
      Until therapy settings are set up, Nocturne uses the default of {hours} hours for insulin on board
      and predictions, not the action time of {insulin}.
    {/if}
  {:else}
    No action time is set, so Nocturne uses the default of {hours} hours for insulin on board and
    predictions.
  {/if}
</p>
