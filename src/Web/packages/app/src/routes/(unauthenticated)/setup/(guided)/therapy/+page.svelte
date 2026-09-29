<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import Check from "@lucide/svelte/icons/check";
  import Info from "@lucide/svelte/icons/info";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import * as Alert from "$lib/components/ui/alert";
  import { Button } from "$lib/components/ui/button";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import ProfileSchedules from "$lib/components/schedule/ProfileSchedules.svelte";
  import {
    confirmTherapySettings,
    getTherapyReview,
  } from "$api/generated/setupTherapies.generated.remote";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import { getInsulinActionTime } from "$api/generated/setupDevices.generated.remote";
  import { InsulinActionTimeNote } from "$lib/components/patient";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { TherapySource } from "$api";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import { formatLongDate } from "$lib/utils/formatting";
  import TherapyEntry from "./TherapyEntry.svelte";

  const reviewQuery = getTherapyReview();
  const relationshipQuery = getPatientRelationship();
  const actionTimeQuery = getInsulinActionTime();
  const review = $derived(reviewQuery.current);
  const voice = $derived(patientVoice(relationshipQuery.current));
  const app = $derived(review?.sourceName ?? undefined);

  const profileHref = resolve("/(authenticated)/settings/profile");

  let mismatch = $state(false);
  let conflicted = $state(false);
  let confirming = $state(false);
  let error = $state<string | undefined>(undefined);

  async function backToHub() {
    await Promise.all([getSetupHub().refresh(), actionTimeQuery.refresh()]);
    await goto(resolve("/setup"));
  }

  async function confirm() {
    confirming = true;
    error = undefined;
    try {
      await confirmTherapySettings();
      await backToHub();
    } catch (err) {
      error = describeSubmitError(err, "We couldn't save that.");
    } finally {
      confirming = false;
    }
  }
</script>

{#if reviewQuery.error}
  <p class="text-sm text-destructive">
    {remoteErrorMessage(reviewQuery.error, "We couldn't load the therapy settings.")}
  </p>
{:else if !review}
  <p class="animate-pulse text-sm text-muted-foreground">Loading therapy settings...</p>
{:else if review.source === TherapySource.None}
  <TherapyEntry
    {voice}
    rules={review.wrongUnitRules ?? []}
    onsaved={backToHub}
    onconflict={async () => {
      conflicted = true;
      await Promise.all([reviewQuery.refresh(), actionTimeQuery.refresh()]);
    }}
    actionTime={actionTimeQuery.current}
  />
{:else}
  <div class="@container flex flex-col gap-6" data-testid="therapy-review" data-source={review.source}>
    {#if conflicted}
      <Alert.Root variant="info" data-testid="therapy-conflict">
        <Info />
        <Alert.Description>
          Therapy settings arrived while you were filling in the form, so what you typed was not
          saved. Here is what arrived.
        </Alert.Description>
      </Alert.Root>
    {/if}
    <div class="flex flex-col gap-2 text-sm leading-relaxed">
      {#if review.source === TherapySource.Synced}
        <p class="text-base font-medium">
          {#if app}
            This is what Nocturne received from {app}.
          {:else}
            This is what Nocturne received from the app that sends these settings.
          {/if}
        </p>
        <p class="text-muted-foreground">
          Check each schedule against the app. Nocturne shows these settings as they arrive and
          does not change them.
        </p>
      {:else if review.source === TherapySource.Imported}
        <p class="text-base font-medium">
          This is what Nocturne imported from your Nightscout site.
        </p>
        {#if review.lastUpdated}
          <p data-testid="therapy-last-updated">
            Last updated {formatLongDate(review.lastUpdated)}.
          </p>
        {/if}
        <p class="text-muted-foreground">
          {#if voice.kind === "self"}
            Settings change over time. Check these against your app today, or the plan your care
            team gave you.
          {:else if voice.kind === "named"}
            Settings change over time. Check these against {voice.name}'s app today, or the plan
            {voice.name}'s care team gave you.
          {:else}
            Settings change over time. Check these against the app in use today, or the plan from
            the care team.
          {/if}
        </p>
      {:else}
        <p class="text-base font-medium">These are the settings you entered.</p>
      {/if}
    </div>

    <ProfileSchedules
      profileName={review.settings?.profileName ?? "Default"}
      basal={review.basal}
      carbRatio={review.carbRatio}
      sensitivity={review.sensitivity}
      targetRange={review.targetRange}
      readOnly
    />

    {#if actionTimeQuery.current}
      <InsulinActionTimeNote
        actionTime={actionTimeQuery.current}
        {voice}
        profileHasNoActionTime={review.source === TherapySource.Entered}
      />
    {/if}

    {#if mismatch}
      <Alert.Root variant="warning" data-testid="therapy-mismatch">
        <RefreshCw />
        <Alert.Title>Where to fix it</Alert.Title>
        <Alert.Description>
          <p>
            {#if review.source === TherapySource.Synced && app}
              If something here doesn't match, change it in {app}. If {app} sends its settings
              again, they replace these, so a change made here would not last.
            {:else if review.source === TherapySource.Synced}
              If something here doesn't match, change it in the app that sent these settings. If
              that app sends its settings again, they replace these.
            {:else if voice.kind === "named"}
              If something here doesn't match, change it in the app {voice.name} uses now. If that
              app sends its settings to Nocturne, they replace these.
            {:else if voice.kind === "self"}
              If something here doesn't match, change it in the app you use now. If that app sends
              its settings to Nocturne, they replace these.
            {:else}
              If something here doesn't match, change it in the app in use now. If that app sends
              its settings to Nocturne, they replace these.
            {/if}
          </p>
          {#if review.source === TherapySource.Imported || !app}
            <p>
              {#if voice.kind === "self"}
                If no app sends your settings to Nocturne, you can find them on the
                <a class="underline underline-offset-2" href={profileHref}>profile page</a> and change the target range there. Basal rates, carb
                ratios and insulin sensitivity can't be changed in Nocturne yet.
              {:else if voice.kind === "named"}
                If no app sends {voice.name}'s settings to Nocturne, you can find them on the
                <a class="underline underline-offset-2" href={profileHref}>profile page</a> and change the target range there. Basal rates, carb
                ratios and insulin sensitivity can't be changed in Nocturne yet.
              {:else}
                If no app sends these settings to Nocturne, you can find them on the
                <a class="underline underline-offset-2" href={profileHref}>profile page</a> and change the target range there. Basal rates, carb
                ratios and insulin sensitivity can't be changed in Nocturne yet.
              {/if}
            </p>
          {/if}
        </Alert.Description>
      </Alert.Root>
    {/if}

    <div class="flex flex-wrap items-center justify-between gap-3">
      <Button variant="link" size="inline" href={profileHref}>
        Open the full profile page
        <ArrowRight class="h-4 w-4" />
      </Button>
      {#if review.source !== TherapySource.Entered && !review.confirmed}
        <div class="flex flex-wrap items-center gap-3">
          <FormError issues={error} />
          {#if !mismatch}
            <Button variant="outline" onclick={() => (mismatch = true)}>
              Something doesn't match
            </Button>
          {/if}
          <Button disabled={confirming} onclick={confirm}>
            <Check class="h-4 w-4" />
            This matches my app
          </Button>
        </div>
      {/if}
    </div>
  </div>
{/if}
