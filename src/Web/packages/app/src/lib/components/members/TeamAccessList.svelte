<script lang="ts">
  import UsersRound from "@lucide/svelte/icons/users-round";
  import TeamAccessCard from "#lib/components/members/TeamAccessCard.svelte";
  import type { TenantRoleDto, TenantTeamAccess } from "@nocturne/api-client";

  interface Props {
    teams: TenantTeamAccess[];
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
    /** The team the page was opened for, from a notice. Its row is scrolled to and focused. */
    linkedTeamId?: string | null;
    stoppingId?: string | null;
    onStop: (teamId: string) => void | Promise<void>;
    onStale: () => void;
    onSuccess: (message: string) => void;
  }

  let {
    teams,
    canStop,
    canDecide,
    roles,
    rolesFailed,
    onRetryRoles,
    limitForced,
    callerAccessEndsAt,
    linkedTeamId = null,
    stoppingId = null,
    onStop,
    onStale,
    onSuccess,
  }: Props = $props();

  let followedId: string | null = null;

  $effect(() => {
    if (!linkedTeamId) {
      followedId = null;
      return;
    }
    if (followedId === linkedTeamId || !teams.some((t) => t.teamId === linkedTeamId)) return;
    followedId = linkedTeamId;
    const row = document.getElementById(`team-${linkedTeamId}`);
    row?.scrollIntoView({ block: "start" });
    row?.focus({ preventScroll: true });
  });
</script>

{#if teams.length > 0}
  <div class="space-y-4">
    <div class="space-y-1">
      <h2 class="flex items-center gap-2 text-lg font-semibold">
        <UsersRound class="h-5 w-5" />
        Teams
      </h2>
      <p class="text-sm text-muted-foreground">
        Staff are the people who work for a team. Approved staff can see this site through the team.
      </p>
    </div>
    {#each teams as team (team.teamId)}
      <TeamAccessCard
        {team}
        {canStop}
        {canDecide}
        {roles}
        {rolesFailed}
        {onRetryRoles}
        {limitForced}
        {callerAccessEndsAt}
        stoppingId={stoppingId ?? null}
        {onStop}
        {onStale}
        {onSuccess}
      />
    {/each}
  </div>
{/if}
