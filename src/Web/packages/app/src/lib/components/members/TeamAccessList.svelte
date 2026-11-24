<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import UsersRound from "@lucide/svelte/icons/users-round";
  import UserMinus from "@lucide/svelte/icons/user-minus";
  import { accessEndDate } from "#lib/components/members/access-end.js";
  import { formatMediumDate, formatMediumDateTime } from "#lib/utils/formatting.js";
  import type { TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    teams: TenantTeamAccess[];
    /** The caller holds members.manage, which stopping a team's access needs. */
    canStop: boolean;
    stoppingId?: string | null;
    onStop: (teamId: string) => void | Promise<void>;
  }

  let { teams, canStop, stoppingId = null, onStop }: Props = $props();
</script>

{#if teams.length > 0}
  <div class="space-y-4">
    <h2 class="flex items-center gap-2 text-lg font-semibold">
      <UsersRound class="h-5 w-5" />
      Teams
    </h2>
    {#each teams as team (team.teamId)}
      <Card.Root>
        <Card.Content class="flex flex-col gap-3 pt-6 @md:flex-row @md:items-start @md:justify-between">
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
          {#if canStop}
            <ConfirmDialog
              title="Stop access"
              confirmLabel="Stop access"
              destructive
              onConfirm={() => onStop(team.teamId ?? "")}
            >
              {#snippet trigger(props)}
                <Button
                  {...props}
                  variant="outline-destructive"
                  size="sm"
                  aria-label={`Stop the access of ${team.teamName}`}
                  disabled={stoppingId !== null}
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
        </Card.Content>
      </Card.Root>
    {/each}
  </div>
{/if}
