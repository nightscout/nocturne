<script lang="ts">
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Check from "@lucide/svelte/icons/check";
  import X from "@lucide/svelte/icons/x";
  import UserX from "@lucide/svelte/icons/user-x";
  import TeamStaffLetBackIn from "#lib/components/members/TeamStaffLetBackIn.svelte";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { formatShortDate } from "#lib/utils/formatting.js";
  import {
    RULE_TEAM_TRUSTED,
    STAFF_APPROVED,
    STAFF_DECLINED,
    STAFF_PENDING,
  } from "#lib/teams/site-staff.svelte.js";
  import type { TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    team: TenantTeamAccess;
    /** The caller is an owner of this site, the only one who approves, declines or stops staff. */
    canDecide: boolean;
    /** The caller, whose own line offers no stop. */
    currentSubjectId?: string;
    /** The command that runs for this team, if any. Every control waits for it. */
    busy: string | null;
    onApprove: (subjectId: string) => void;
    onDecline: (subjectId: string) => void;
    /** Stops the person seeing this site through every team, under every rule. */
    onBlock: (subjectId: string) => void;
    onUnblock: (subjectId: string) => void;
  }

  let { team, canDecide, currentSubjectId, busy, onApprove, onDecline, onBlock, onUnblock }: Props = $props();

  const staff = $derived(team.staff ?? []);
  const trusted = $derived(team.staffRule === RULE_TEAM_TRUSTED);
  const decides = $derived(canDecide && !trusted);
</script>

<div class="space-y-2">
  <h3 class="text-sm font-medium">Staff</h3>
  {#if canDecide && trusted}
    <p class="text-sm text-muted-foreground">
      You trust the whole team, so you do not approve or decline one staff member. You can still stop one person's
      access.
    </p>
  {/if}
  {#if staff.length === 0}
    <p class="text-sm text-muted-foreground">This team has no staff.</p>
  {:else}
    <ul class="divide-y border-y" aria-label={`Staff of ${team.teamName}`}>
      {#each staff as member (member.subjectId)}
        {@const name = member.displayName ?? "Unknown"}
        {@const ceiling = member.roleCeilingName}
        {@const endsAt = member.narrowing?.endsAt}
        <li class="flex flex-col gap-2 py-3 @md:flex-row @md:items-start @md:justify-between">
          <div class="min-w-0 space-y-1">
            <div class="flex flex-wrap items-center gap-2">
              <span class="truncate text-sm font-medium">{name}</span>
              {#if member.blocked}
                <Badge variant="outline">Stopped</Badge>
              {:else if member.status === STAFF_APPROVED}
                <Badge variant="secondary">Approved</Badge>
              {:else if member.status === STAFF_PENDING && trusted}
                <Badge variant="outline">Not approved</Badge>
              {:else if member.status === STAFF_PENDING && canDecide}
                <Badge variant="warning">Waits for your approval</Badge>
              {:else if member.status === STAFF_PENDING}
                <Badge variant="warning">Waits for an owner's approval</Badge>
              {:else if member.status === STAFF_DECLINED}
                <Badge variant="outline">Declined</Badge>
              {/if}
            </div>
            {#if member.blocked}
              <p class="text-xs text-muted-foreground">Cannot see this site through any team.</p>
            {:else if trusted && member.status !== STAFF_APPROVED && canDecide}
              <p class="text-xs text-muted-foreground">Can see this site while you trust the whole team.</p>
            {:else if trusted && member.status !== STAFF_APPROVED}
              <p class="text-xs text-muted-foreground">Can see this site while the site trusts the whole team.</p>
            {/if}
            {#if ceiling || member.narrowing?.limitTo24Hours || endsAt}
              <ul class="flex flex-wrap gap-x-3 text-xs text-muted-foreground" aria-label={`What limits ${name}`}>
                {#if ceiling}
                  <li>Up to the role {ceiling}</li>
                {/if}
                {#if member.narrowing?.limitTo24Hours}
                  <li>Last 24 hours only</li>
                {/if}
                {#if endsAt && member.hasEnded}
                  <li>Time on the team ended {formatShortDate(accessEndDate(endsAt), true)}</li>
                {:else if endsAt}
                  <li>On the team until {formatShortDate(accessEndDate(endsAt), true)}</li>
                {/if}
              </ul>
            {/if}
          </div>
          {#if canDecide}
            <div class="flex shrink-0 flex-wrap gap-2">
              {#if member.blocked}
                <TeamStaffLetBackIn
                  {name}
                  disabled={busy !== null}
                  working={busy === `unblock:${member.subjectId}`}
                  onConfirm={() => onUnblock(member.subjectId ?? "")}
                />
              {/if}
              {#if decides && !member.blocked && (member.status === STAFF_PENDING || member.status === STAFF_DECLINED)}
                <Button
                  variant="outline"
                  size="sm"
                  aria-label={`Approve ${name}`}
                  disabled={busy !== null}
                  onclick={() => onApprove(member.subjectId ?? "")}
                >
                  {#if busy === `approve:${member.subjectId}`}
                    <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <Check class="mr-1.5 h-3.5 w-3.5" />
                  {/if}
                  Approve
                </Button>
              {/if}
              {#if decides && !member.blocked && (member.status === STAFF_PENDING || member.status === STAFF_APPROVED)}
                <ConfirmDialog
                  title={`Decline ${name}?`}
                  confirmLabel="Decline"
                  destructive
                  onConfirm={() => onDecline(member.subjectId ?? "")}
                >
                  {#snippet trigger(props)}
                    <Button
                      {...props}
                      variant="outline-destructive"
                      size="sm"
                      aria-label={`Decline ${name}`}
                      disabled={busy !== null}
                    >
                      {#if busy === `decline:${member.subjectId}`}
                        <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                      {:else}
                        <X class="mr-1.5 h-3.5 w-3.5" />
                      {/if}
                      Decline
                    </Button>
                  {/snippet}
                  {#snippet description()}
                    They do not see this site's data through the team. They stay declined if they leave the
                    team and join again, until you approve them or stop the team's access.
                  {/snippet}
                </ConfirmDialog>
              {/if}
              {#if !member.blocked && member.subjectId !== currentSubjectId}
                <ConfirmDialog
                  title={`Stop ${name}'s access?`}
                  confirmLabel="Stop access"
                  destructive
                  onConfirm={() => onBlock(member.subjectId ?? "")}
                >
                  {#snippet trigger(props)}
                    <Button
                      {...props}
                      variant="outline-destructive"
                      size="sm"
                      aria-label={`Stop ${name}'s access`}
                      disabled={busy !== null}
                    >
                      {#if busy === `block:${member.subjectId}`}
                        <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                      {:else}
                        <UserX class="mr-1.5 h-3.5 w-3.5" />
                      {/if}
                      Stop access
                    </Button>
                  {/snippet}
                  {#snippet description()}
                    {name} stops seeing this site's data through every team, now and later. Access they have through
                    you directly does not change. You can undo this.
                  {/snippet}
                </ConfirmDialog>
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</div>
