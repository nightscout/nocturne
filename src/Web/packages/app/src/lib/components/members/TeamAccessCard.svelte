<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Settings2 from "@lucide/svelte/icons/settings-2";
  import UserMinus from "@lucide/svelte/icons/user-minus";
  import { FormError } from "#lib/forms/index.js";
  import { describeRemoteError, errorStatus, type ProblemCodeMessages } from "#lib/forms/submit-error.js";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import TeamStaffList from "#lib/components/members/TeamStaffList.svelte";
  import TeamStaffRule from "#lib/components/members/TeamStaffRule.svelte";
  import TeamAccessEditForm from "#lib/components/members/TeamAccessEditForm.svelte";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import {
    teamAccessEditProblemMessages,
    teamStaffDecisionProblemMessages,
  } from "#lib/teams/problem-messages.svelte.js";
  import {
    approveTeamStaff,
    declineTeamStaff,
    editTeamAccess,
    setTeamStaffRule,
  } from "#lib/api/generated/memberInvites.generated.remote.js";
  import type { TenantRoleDto, TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    team: TenantTeamAccess;
    /** The caller holds members.manage, which stopping a team's access needs. */
    canStop: boolean;
    /** The caller is an owner of this site, the only one who decides about a team and its staff. */
    canDecide: boolean;
    /** The site's roles, each saying whether the caller may give it to a team; undefined until they load. */
    roles: TenantRoleDto[] | undefined;
    /** The site's roles did not load. */
    rolesFailed: boolean;
    onRetryRoles: () => void;
    /** The caller reads only the last 24 hours, so the access they set must keep that limit. */
    limitForced: boolean;
    /** When the caller's direct access ends, which bounds the end they set. */
    callerAccessEndsAt?: string;
    stoppingId: string | null;
    onStop: (teamId: string) => void | Promise<void>;
    /** A command found the team or a staff member gone, so the list is out of date. */
    onStale: () => void;
    onSuccess: (message: string) => void;
  }

  let {
    team,
    canStop,
    canDecide,
    roles,
    rolesFailed,
    onRetryRoles,
    limitForced,
    callerAccessEndsAt,
    stoppingId,
    onStop,
    onStale,
    onSuccess,
  }: Props = $props();

  const teamId = $derived(team.teamId ?? "");

  let busy = $state<string | null>(null);
  let error = $state<string | null>(null);
  let editing = $state(false);

  async function run(
    key: string,
    action: () => Promise<unknown>,
    done: string,
    failed: string,
    missing: string,
    codes: ProblemCodeMessages,
  ): Promise<boolean> {
    busy = key;
    error = null;
    try {
      await action();
      onSuccess(done);
      return true;
    } catch (err) {
      error = describeRemoteError(
        err,
        failed,
        { missing: () => missing, forbiddenReason: true, serverError: false },
        codes,
      );
      if (errorStatus(err) === 404) onStale();
      return false;
    } finally {
      busy = null;
    }
  }

  const staffGone = "This team no longer has access, or this person is no longer on its staff.";
  const teamGone = "This team no longer has access.";

  function approve(subjectId: string) {
    void run(
      `approve:${subjectId}`,
      () => approveTeamStaff({ teamId, subjectId }),
      "Staff member approved.",
      "Could not approve the staff member. Try again.",
      staffGone,
      teamStaffDecisionProblemMessages(),
    );
  }

  function decline(subjectId: string) {
    void run(
      `decline:${subjectId}`,
      () => declineTeamStaff({ teamId, subjectId }),
      "Staff member declined.",
      "Could not decline the staff member. Try again.",
      staffGone,
      teamStaffDecisionProblemMessages(),
    );
  }

  function setRule(teamManagesStaff: boolean) {
    void run(
      "rule",
      () => setTeamStaffRule({ teamId, request: { teamManagesStaff } }),
      teamManagesStaff ? "The team now decides who on its staff can see this site." : "Each new staff member now waits for an owner's approval.",
      "Could not change the setting. Try again.",
      teamGone,
      teamStaffDecisionProblemMessages(),
    );
  }

  async function save(request: Parameters<typeof editTeamAccess>[0]["request"]) {
    const saved = await run(
      "edit",
      () => editTeamAccess({ teamId, request }),
      "Access of the team changed.",
      "Could not change the access of the team. Try again.",
      teamGone,
      teamAccessEditProblemMessages(),
    );
    if (saved) editing = false;
  }
</script>

<div
  id={`team-${teamId}`}
  tabindex="-1"
  class="scroll-mt-6 rounded-xl outline-none focus:ring-3 focus:ring-ring/50"
  role="group"
  aria-label={team.teamName}
>
  <Card.Root>
    <Card.Content class="space-y-4 pt-6">
      <div class="flex flex-col gap-3 @md:flex-row @md:items-start @md:justify-between">
        <div class="min-w-0 space-y-1">
          <p class="truncate font-medium">{team.teamName}</p>
          <p class="text-sm text-muted-foreground">
            Roles: {(team.roles ?? []).map((r) => r.name).join(", ")}
          </p>
          <p class="text-sm text-muted-foreground">
            {team.limitTo24Hours ? "Only the last 24 hours" : "All history"}
            ·
            {#if !team.accessEndsAt}
              Access does not end
            {:else if team.hasEnded}
              Access ended {formatMediumDate(accessEndDate(team.accessEndsAt))}
            {:else}
              Access ends {formatMediumDate(accessEndDate(team.accessEndsAt))}
            {/if}
          </p>
          <p class="text-sm text-muted-foreground">
            Approved by {team.approvedByName ?? "a deleted member"} · {formatMediumDateTime(team.givenAt)}
          </p>
        </div>
        <div class="flex shrink-0 flex-wrap gap-2">
          {#if canDecide && !editing && roles}
            <Button
              variant="outline"
              size="sm"
              aria-label={`Change the access of ${team.teamName}`}
              disabled={busy !== null}
              onclick={() => {
                error = null;
                editing = true;
              }}
            >
              <Settings2 class="mr-2 h-4 w-4" />
              Change access
            </Button>
          {/if}
          {#if canStop}
            <ConfirmDialog
              title="Stop access"
              confirmLabel="Stop access"
              destructive
              onConfirm={() => onStop(teamId)}
            >
              {#snippet trigger(props)}
                <Button
                  {...props}
                  variant="outline-destructive"
                  size="sm"
                  aria-label={`Stop the access of ${team.teamName}`}
                  disabled={stoppingId !== null || busy !== null}
                >
                  {#if stoppingId === team.teamId}
                    <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                  {:else}
                    <UserMinus class="mr-2 h-4 w-4" />
                  {/if}
                  Stop access
                </Button>
              {/snippet}
              {#snippet description()}
                Stop the access of {team.teamName}? The staff of the team lose access at once. A manager of
                the team must ask again.
              {/snippet}
            </ConfirmDialog>
          {/if}
        </div>
      </div>

      {#if canDecide && !roles && rolesFailed}
        <div class="space-y-2">
          <FormError issues="The roles of this site did not load, so you cannot change the access of this team." />
          <Button variant="outline" size="sm" onclick={onRetryRoles}>Try again</Button>
        </div>
      {/if}

      {#if canDecide && editing && roles}
        <Separator />
        <TeamAccessEditForm
          {team}
          {roles}
          {limitForced}
          {callerAccessEndsAt}
          saving={busy === "edit"}
          onSave={save}
          onCancel={() => {
            error = null;
            editing = false;
          }}
        />
      {/if}

      <Separator />
      <TeamStaffRule {team} {canDecide} {busy} onChange={setRule} />
      <TeamStaffList {team} {canDecide} {busy} onApprove={approve} onDecline={decline} />

      <FormError issues={error} focusOnShow />
    </Card.Content>
  </Card.Root>
</div>
