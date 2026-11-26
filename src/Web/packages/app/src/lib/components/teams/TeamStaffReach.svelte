<script lang="ts">
  import { tick } from "svelte";
  import * as RadioGroup from "#lib/components/ui/radio-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { setStaffReach } from "#lib/api/generated/teams.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { plural } from "#lib/utils/plural.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import { REACH_EVERY_PERSON, REACH_THEIR_GROUPS } from "#lib/teams/staff-reach.svelte.js";

  interface Props {
    teamId: string;
    /** A TeamStaffReach value. */
    staffReach: string;
    /** How many accesses of a staff member to a person stop if the team narrows to its groups now. */
    accessesOutsideGroups: number;
  }

  let { teamId, staffReach, accessesOutsideGroups }: Props = $props();

  const labelId = "team-staff-reach-label";

  // A radio takes the choice when it takes focus, and a closing dialog gives focus back to what opened it,
  // so a choice is only marked here and its own button opens the confirmation.
  let target = $state<string | null>(null);
  let saving = $state(false);
  let error = $state<string | null>(null);
  let root = $state<HTMLElement | null>(null);

  async function focusChecked() {
    await tick();
    root?.querySelector<HTMLElement>('[role="radio"][aria-checked="true"]')?.focus();
  }

  function keep() {
    target = null;
    void focusChecked();
  }

  async function confirm() {
    const next = target;
    if (!next || saving) return;
    saving = true;
    error = null;
    try {
      await setStaffReach({ teamId, request: { staffReach: next } });
      target = null;
    } catch (err) {
      error = describeSubmitError(err, "Could not change the setting. Try again.", teamProblemMessages());
    } finally {
      saving = false;
    }
    void focusChecked();
  }
</script>

<div class="space-y-2">
  <Label id={labelId}>Which people the staff see</Label>
  <RadioGroup.Root
    bind:ref={root}
    class="gap-2"
    aria-labelledby={labelId}
    value={target ?? staffReach}
    onValueChange={(next) => (target = next === staffReach ? null : next)}
    disabled={saving}
  >
    <RadioGroup.Card value={REACH_EVERY_PERSON}>
      <span class="text-sm font-medium">Staff see every person the team follows</span>
      <span class="text-xs text-muted-foreground">Groups do not change what the staff see.</span>
    </RadioGroup.Card>
    <RadioGroup.Card value={REACH_THEIR_GROUPS}>
      <span class="text-sm font-medium">Staff see only the people in their groups</span>
      <span class="text-xs text-muted-foreground">
        A staff member sees a person through the team only when they are both in one group.
      </span>
    </RadioGroup.Card>
  </RadioGroup.Root>
  <p class="text-xs text-muted-foreground">Each site still decides which staff of the team can see it.</p>
  {#if error}
    <p class="text-sm text-destructive" role="alert">{error}</p>
  {/if}
  {#if target !== null}
    <div class="flex flex-wrap gap-2">
      <ConfirmDialog
        title={target === REACH_THEIR_GROUPS
          ? "Let staff see only the people in their groups?"
          : "Let staff see every person the team follows?"}
        confirmLabel="Change setting"
        busy={saving}
        onConfirm={confirm}
      >
        {#snippet trigger(props)}
          <Button {...props} size="sm" disabled={saving}>
            {#if saving}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Change setting
          </Button>
        {/snippet}
        {#snippet description()}
          <span class="block space-y-1">
            {#if target === REACH_THEIR_GROUPS}
              <span class="block">
                {#if accessesOutsideGroups > 0}
                  {plural(accessesOutsideGroups, [
                    "Staff lose # access through the team to a person that they are not in a group with.",
                    "Staff lose # accesses through the team to people that they are not in a group with.",
                  ])}
                {:else}
                  No staff member loses access through the team now.
                {/if}
              </span>
              <span class="block">A new staff member sees no one through the team until you add them to a group.</span>
            {:else}
              <span class="block">Staff see every person that the team follows again, as each site allows.</span>
            {/if}
          </span>
        {/snippet}
      </ConfirmDialog>
      <Button variant="outline" size="sm" disabled={saving} onclick={keep}>Keep the setting</Button>
    </div>
  {/if}
</div>
