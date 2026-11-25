<script lang="ts">
  import { tick } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import TeamStaffRuleChoices from "#lib/components/teams/TeamStaffRuleChoices.svelte";
  import { plural } from "#lib/utils/plural.js";
  import {
    RULE_OWNER_APPROVES,
    RULE_TEAM_DECIDES,
    RULE_TEAM_TRUSTED,
    STAFF_DECLINED,
    STAFF_PENDING,
  } from "#lib/teams/site-staff.svelte.js";
  import type { TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    team: TenantTeamAccess;
    /** The caller is an owner of this site, the only one who changes the setting. */
    canDecide: boolean;
    /** The command that runs for this team, if any. The setting waits for it. */
    busy: string | null;
    onChange: (staffRule: string) => void;
  }

  let { team, canDecide, busy, onChange }: Props = $props();

  const current = $derived(team.staffRule ?? RULE_TEAM_DECIDES);
  const waiting = $derived((team.staff ?? []).filter((s) => s.status === STAFF_PENDING).length);
  const declined = $derived((team.staff ?? []).filter((s) => s.status === STAFF_DECLINED).length);
  const leavesTrust = $derived(current === RULE_TEAM_TRUSTED);

  // A radio takes the choice when it takes focus, and a closing dialog gives focus back to what opened it,
  // so a choice is only marked here and its own button opens the confirmation.
  let target = $state<string | null>(null);
  let choices = $state<ReturnType<typeof TeamStaffRuleChoices> | null>(null);
  // Closing the choice removes the buttons and the confirmation's trigger, so focus would fall to the page.
  let refocus = $state(false);

  $effect(() => {
    if (!refocus || busy !== null) return;
    refocus = false;
    void tick().then(() => choices?.focusChecked());
  });

  function close() {
    target = null;
    refocus = true;
  }

  function confirm() {
    const next = target;
    close();
    if (next) onChange(next);
  }
</script>

{#if canDecide}
  <div class="space-y-2">
    <TeamStaffRuleChoices
      bind:this={choices}
      idPrefix={`team-${team.teamId}`}
      value={target ?? current}
      onChange={(next) => (target = next === current ? null : next)}
      disabled={busy !== null}
    />
    {#if busy === "rule"}
      <Loader2 class="h-3.5 w-3.5 animate-spin" />
    {/if}
    {#if target !== null}
      <div class="flex flex-wrap gap-2">
        <ConfirmDialog
          title={target === RULE_OWNER_APPROVES
            ? "Approve each staff member yourself?"
            : target === RULE_TEAM_TRUSTED
              ? "Trust the whole team?"
              : "Let the team decide?"}
          confirmLabel="Change setting"
          onConfirm={confirm}
        >
          {#snippet trigger(props)}
            <Button {...props} size="sm" disabled={busy !== null}>Change setting</Button>
          {/snippet}
          {#snippet description()}
            <span class="block space-y-1">
              {#if target === RULE_OWNER_APPROVES}
                <span class="block">
                  Each new staff member of {team.teamName} waits for your approval. Approved staff keep their access.
                </span>
              {:else if target === RULE_TEAM_TRUSTED}
                <span class="block">
                  Every staff member of {team.teamName} can see this site. You get no notices about them.
                </span>
                {#if waiting > 0}
                  <span class="block">
                    {plural(waiting, [
                      "# waiting staff member gets access while you trust the whole team.",
                      "# waiting staff members get access while you trust the whole team.",
                    ])}
                  </span>
                {/if}
                {#if declined > 0}
                  <span class="block">
                    {plural(declined, [
                      "# declined staff member gets access while you trust the whole team.",
                      "# declined staff members get access while you trust the whole team.",
                    ])}
                  </span>
                {/if}
              {:else}
                <span class="block">New staff of {team.teamName} can see this site at once.</span>
                {#if waiting > 0}
                  <span class="block">
                    {plural(waiting, ["# waiting staff member is approved.", "# waiting staff members are approved."])}
                  </span>
                {/if}
              {/if}
              {#if leavesTrust && target === RULE_OWNER_APPROVES && waiting > 0}
                <span class="block">
                  {plural(waiting, [
                    "# waiting staff member loses access until you approve them.",
                    "# waiting staff members lose access until you approve them.",
                  ])}
                </span>
              {/if}
              {#if leavesTrust && declined > 0}
                <span class="block">
                  {plural(declined, [
                    "# declined staff member loses access at once.",
                    "# declined staff members lose access at once.",
                  ])}
                </span>
              {/if}
            </span>
          {/snippet}
        </ConfirmDialog>
        <Button variant="outline" size="sm" disabled={busy !== null} onclick={close}>
          Keep the setting
        </Button>
      </div>
    {/if}
  </div>
{:else if current === RULE_TEAM_TRUSTED}
  <p class="text-sm text-muted-foreground">This site trusts the whole team. Every staff member can see this site.</p>
{:else if current === RULE_TEAM_DECIDES}
  <p class="text-sm text-muted-foreground">The team decides who on its staff can see this site.</p>
{:else}
  <p class="text-sm text-muted-foreground">An owner of this site approves each staff member of this team.</p>
{/if}
