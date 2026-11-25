<script lang="ts">
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { STAFF_PENDING } from "#lib/teams/site-staff.svelte.js";
  import type { TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    team: TenantTeamAccess;
    /** The caller is an owner of this site, the only one who turns the switch. */
    canDecide: boolean;
    /** The command that runs for this team, if any. The switch waits for it. */
    busy: string | null;
    onChange: (teamManagesStaff: boolean) => void;
  }

  let { team, canDecide, busy, onChange }: Props = $props();

  const on = $derived(team.teamManagesStaff === true);
  const waiting = $derived((team.staff ?? []).filter((s) => s.status === STAFF_PENDING).length);
  const switchId = $derived(`team-staff-rule-${team.teamId}`);
  const helpId = $derived(`${switchId}-help`);

  let confirmingOn = $state(false);

  function request(next: boolean) {
    if (next && waiting > 0) {
      confirmingOn = true;
      return;
    }
    onChange(next);
  }
</script>

{#if canDecide}
  <div class="flex items-start gap-3">
    <Switch
      id={switchId}
      bind:checked={() => on, request}
      disabled={busy !== null}
      aria-describedby={helpId}
    />
    <div class="flex-1 space-y-1">
      <div class="flex items-center gap-2">
        <Label for={switchId}>The team decides who on its staff can see this site</Label>
        {#if busy === "rule"}
          <Loader2 class="h-3.5 w-3.5 animate-spin" />
        {/if}
      </div>
      <div id={helpId} class="space-y-1 text-xs text-muted-foreground">
        {#if on}
          <p>On: new staff, and staff that the team gives more access, can see this site at once.</p>
        {:else}
          <p>
            Off: each new staff member, and each one that the team gives more access, waits for an
            owner's approval.
          </p>
        {/if}
        <p>
          If you change it to off, approved staff keep their access. If you change it to on, all waiting
          staff are approved.
        </p>
      </div>
    </div>
  </div>
  <ConfirmDialog
    bind:open={confirmingOn}
    title="Approve all waiting staff?"
    confirmLabel="Approve all"
    onConfirm={() => onChange(true)}
  >
    {#snippet description()}
      {#if waiting === 1}
        1 staff member of {team.teamName} waits for approval. If you change this setting to on, they are
        approved.
      {:else}
        {waiting} staff members of {team.teamName} wait for approval. If you change this setting to on, they
        are all approved.
      {/if}
    {/snippet}
  </ConfirmDialog>
{:else if on}
  <p class="text-sm text-muted-foreground">The team decides who on its staff can see this site.</p>
{:else}
  <p class="text-sm text-muted-foreground">An owner of this site approves each staff member of this team.</p>
{/if}
