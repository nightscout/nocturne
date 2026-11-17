<script lang="ts">
  import {
    createActivation,
    getActivationStatus,
  } from "#lib/api/generated/profileActivations.generated.remote.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { profileActivationSource } from "#lib/utils/profile-activation-label.js";
  import Info from "@lucide/svelte/icons/info";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { describeSubmitError } from "#lib/forms/index.js";

  interface Props {
    open: boolean;
    profileName: string;
  }

  let { open = $bindable(false), profileName }: Props = $props();

  let percentage = $state(100);
  let timeshiftHours = $state(0);
  let temporary = $state(false);
  let durationValue = $state(60);
  let durationUnit = $state<"minutes" | "hours">("minutes");
  let notes = $state("");
  let submitting = $state(false);
  let submitError = $state<string | null>(null);

  const statusQuery = getActivationStatus(undefined);
  const reported = $derived(
    statusQuery.current?.recordedSwitchesApply === false ? statusQuery.current.reported : undefined
  );

  const percentageValid = $derived(
    Number.isInteger(percentage) && percentage >= 30 && percentage <= 250
  );
  const timeshiftValid = $derived(
    Number.isInteger(timeshiftHours) && timeshiftHours >= -23 && timeshiftHours <= 23
  );
  const durationMinutes = $derived(
    durationUnit === "hours" ? durationValue * 60 : durationValue
  );
  const durationValid = $derived(
    !temporary ||
      (Number.isInteger(durationMinutes) && durationMinutes > 0 && durationMinutes <= 10080)
  );
  const canSubmit = $derived(
    percentageValid && timeshiftValid && durationValid && !submitting
  );

  function reset() {
    percentage = 100;
    timeshiftHours = 0;
    temporary = false;
    durationValue = 60;
    durationUnit = "minutes";
    notes = "";
    submitError = null;
  }

  function close() {
    open = false;
    reset();
  }

  async function submit() {
    if (!canSubmit) return;
    submitting = true;
    submitError = null;
    try {
      await createActivation({
        baseProfileName: profileName,
        percentage,
        timeshiftHours,
        durationMinutes: temporary ? durationMinutes : null,
        notes: notes.trim() === "" ? null : notes,
      });
      close();
    } catch (err) {
      submitError = describeSubmitError(err, "The switch was not recorded.");
    } finally {
      submitting = false;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(next: boolean) => { if (!next) reset(); }}>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title>Switch to {profileName}</Dialog.Title>
      <Dialog.Description>
        Starts now. Scales the profile by a percentage and shifts its schedule by whole hours.
      </Dialog.Description>
    </Dialog.Header>

    <Alert.Root>
      <Info class="h-4 w-4" />
      <Alert.Description>
        The switch is recorded in Nocturne only. It is not sent to your pump, and AAPS and
        Nightscout followers never see it.
      </Alert.Description>
    </Alert.Root>

    {#if reported}
      <Alert.Root variant="warning">
        <TriangleAlert class="h-4 w-4" />
        <Alert.Description>
          {profileActivationSource(reported) ?? "An AID system"} is reporting the profile your pump
          runs. The switch will be recorded, but Nocturne's calculations follow the reported profile
          while it does.
        </Alert.Description>
      </Alert.Root>
    {/if}

    <div class="grid gap-4">
      <div class="grid gap-2">
        <Label for="profile-switch-percentage">Percentage</Label>
        <Input
          id="profile-switch-percentage"
          type="number"
          min={30}
          max={250}
          step={5}
          bind:value={percentage}
          aria-invalid={!percentageValid}
        />
        {#if !percentageValid}
          <p class="text-xs text-destructive">Enter a whole number from 30 to 250.</p>
        {/if}
      </div>

      <div class="grid gap-2">
        <Label for="profile-switch-timeshift">Timeshift (hours)</Label>
        <Input
          id="profile-switch-timeshift"
          type="number"
          min={-23}
          max={23}
          step={1}
          bind:value={timeshiftHours}
          aria-invalid={!timeshiftValid}
        />
        {#if !timeshiftValid}
          <p class="text-xs text-destructive">Enter a whole number from -23 to 23.</p>
        {/if}
      </div>

      <div class="grid gap-2">
        <div class="flex items-center gap-2">
          <Checkbox id="profile-switch-temporary" bind:checked={temporary} />
          <Label for="profile-switch-temporary">Temporary</Label>
        </div>
        {#if temporary}
          <div class="flex items-center gap-2">
            <Input
              id="profile-switch-duration"
              type="number"
              min={1}
              step={1}
              class="w-28"
              aria-label="Duration"
              bind:value={durationValue}
              aria-invalid={!durationValid}
            />
            <ToggleGroup.Root
              type="single"
              value={durationUnit}
              onValueChange={(next: string) => {
                if (next === "minutes" || next === "hours") durationUnit = next;
              }}
              variant="segmented"
              size="xs"
            >
              <ToggleGroup.Item value="minutes">Minutes</ToggleGroup.Item>
              <ToggleGroup.Item value="hours">Hours</ToggleGroup.Item>
            </ToggleGroup.Root>
          </div>
          {#if !durationValid}
            <p class="text-xs text-destructive">Enter a length from 1 minute to 7 days.</p>
          {/if}
        {:else}
          <p class="text-xs text-muted-foreground">Runs until another switch replaces it or it is ended.</p>
        {/if}
      </div>

      <div class="grid gap-2">
        <Label for="profile-switch-notes">Notes</Label>
        <Textarea id="profile-switch-notes" rows={2} maxlength={2000} bind:value={notes} />
      </div>

      {#if submitError}
        <p class="text-sm text-destructive" role="alert">{submitError}</p>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={close}>Cancel</Button>
      <Button onclick={submit} disabled={!canSubmit}>
        {#if submitting}
          <Loader2 class="h-4 w-4 animate-spin" />
        {/if}
        Record switch
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
