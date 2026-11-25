<script lang="ts">
  import {
    MemberHistoryActorKind,
    MemberHistoryKind,
    MemberHistoryLimit,
    MembershipEndHow,
    MembershipJoinHow,
    type MemberHistoryActor,
    type MemberHistoryEntry,
    type TenantRoleDto,
  } from "@nocturne/api-client";
  import { scopeLabels } from "#lib/constants/scope-labels.svelte.js";
  import { formatDateTime, formatMediumDate } from "#lib/utils/formatting.js";
  import { accessEndDate } from "#lib/components/members/access-end.js";

  interface Props {
    entry: MemberHistoryEntry;
    roles?: TenantRoleDto[];
  }

  let { entry, roles = [] }: Props = $props();

  const labels = $derived<Record<string, string>>(scopeLabels());

  function actorName(actor: MemberHistoryActor | undefined): string {
    switch (actor?.kind) {
      case MemberHistoryActorKind.Person:
        return actor.name ?? "A deleted member";
      case MemberHistoryActorKind.System:
        return "The system";
      case MemberHistoryActorKind.Anonymous:
        return "A caller who was not signed in";
      case MemberHistoryActorKind.ApiKey:
        return "An API key";
      case MemberHistoryActorKind.AccessToken:
        return "An access token";
      case MemberHistoryActorKind.InstanceKey:
        return "An instance key";
      case MemberHistoryActorKind.GuestLink:
        return "A guest link";
      case MemberHistoryActorKind.PlatformAccess:
        return "A platform administrator";
      default:
        return "Someone unknown";
    }
  }

  const actor = $derived(actorName(entry.actor));
  const member = $derived(entry.member?.name ?? "a deleted member");
  const memberFirst = $derived(entry.member?.name ?? "A deleted member");
  const by = $derived(entry.by ? (entry.by.name ?? "a deleted member") : undefined);
  const approver = $derived(entry.by ? (entry.by.name ?? "A deleted member") : actor);
  const role = $derived(
    roles.find((r) => r.slug === entry.role)?.name ?? entry.role ?? "",
  );
  const permission = $derived(
    labels[entry.permission ?? ""] ?? entry.permission ?? "",
  );
  const accessEnd = $derived(
    entry.accessEndsAt ? formatMediumDate(accessEndDate(entry.accessEndsAt)) : "",
  );
  /**
   * A membership made by an invite is recorded with the person who joined as its actor, so "gave"
   * would name them as the giver of their own access. A join with its own Joined entry already
   * names the invite, so its changes do not repeat it.
   */
  const joined = $derived(
    !!entry.viaInvite &&
      !!entry.member?.subjectId &&
      entry.actor?.subjectId === entry.member.subjectId,
  );
  const teamRoleCount = $derived(entry.roles?.length ?? 0);
  const teamRoles = $derived(
    (entry.roles ?? []).map((slug) => roles.find((r) => r.slug === slug)?.name ?? slug).join(", "),
  );
  const teamLimited = $derived(entry.historyLimit === MemberHistoryLimit.TwentyFourHours);
  const gained = $derived((entry.gained ?? []).map((p) => labels[p] ?? p).join(", "));
  const lost = $derived((entry.lost ?? []).map((p) => labels[p] ?? p).join(", "));
</script>

<li class="@container flex flex-col gap-1 p-4 @md:flex-row @md:gap-6">
  <time
    class="shrink-0 whitespace-nowrap text-sm text-muted-foreground tabular-nums @md:w-40"
    datetime={entry.at}
  >
    {formatDateTime(entry.at)}
  </time>
  <div class="min-w-0 flex-1 space-y-1 text-sm">
    <p>
      {#if entry.kind === MemberHistoryKind.Joined}
        {#if entry.joinedHow === MembershipJoinHow.Invite}
          {#if entry.inviteLabel && by}
            {memberFirst} joined through the invite {entry.inviteLabel}, made by {by}.
          {:else if by}
            {memberFirst} joined through an invite made by {by}.
          {:else if entry.inviteLabel}
            {memberFirst} joined through the invite {entry.inviteLabel}.
          {:else}
            {memberFirst} joined through an invite.
          {/if}
        {:else if entry.joinedHow === MembershipJoinHow.Request}
          {approver} approved {member}'s request to join.
        {:else if entry.joinedHow === MembershipJoinHow.Direct}
          {actor} added {member}.
        {:else if entry.joinedHow === MembershipJoinHow.Setup}
          {memberFirst} set up this site.
        {:else if entry.joinedHow === MembershipJoinHow.System}
          The system added {member}.
        {:else if entry.joinedHow === MembershipJoinHow.Team && entry.team?.name}
          {memberFirst} now has access through the team {entry.team.name}.
        {:else if entry.joinedHow === MembershipJoinHow.Team}
          {memberFirst} now has access through a team. The detail is no longer stored.
        {:else}
          {memberFirst} joined. The detail is no longer stored.
        {/if}
      {:else if entry.kind === MemberHistoryKind.Removed}
        {#if entry.removedHow === MembershipEndHow.Removed}
          {actor} removed {member}.
        {:else if entry.removedHow === MembershipEndHow.Left}
          {memberFirst} left.
        {:else if entry.removedHow === MembershipEndHow.AccountDeleted}
          {memberFirst}'s account was deleted.
        {:else if entry.removedHow === MembershipEndHow.Replaced}
          {memberFirst} joined again. The earlier membership was replaced.
        {:else if entry.removedHow === MembershipEndHow.System}
          The system removed {member}.
        {:else if entry.removedHow === MembershipEndHow.Team && entry.team?.name}
          {memberFirst} no longer has access through the team {entry.team.name}.
        {:else if entry.removedHow === MembershipEndHow.Team}
          {memberFirst} no longer has access through a team. The detail is no longer stored.
        {:else}
          {memberFirst}'s membership ended. The detail is no longer stored.
        {/if}
      {:else if joined && entry.kind === MemberHistoryKind.RoleGiven}
        {actor} joined with the role {role}.
      {:else if joined && entry.kind === MemberHistoryKind.PermissionGiven}
        {actor} joined with the permission {permission}.
      {:else if joined && entry.kind === MemberHistoryKind.AccessGiven}
        {actor} joined with a role or a permission. The detail is no longer stored.
      {:else if joined && entry.historyLimit === MemberHistoryLimit.TwentyFourHours}
        {actor} joined with access to the last 24 hours only.
      {:else if joined && entry.kind === MemberHistoryKind.AccessEndSet}
        {actor} joined with access that ends on {accessEnd}.
      {:else if entry.kind === MemberHistoryKind.RoleGiven}
        {actor} gave {member} the role {role}.
      {:else if entry.kind === MemberHistoryKind.RoleTaken}
        {actor} removed the role {role} from {member}.
      {:else if entry.kind === MemberHistoryKind.PermissionGiven}
        {actor} gave {member} the permission {permission}.
      {:else if entry.kind === MemberHistoryKind.PermissionTaken}
        {actor} removed the permission {permission} from {member}.
      {:else if entry.kind === MemberHistoryKind.AccessGiven}
        {actor} gave {member} a role or a permission. The detail is no longer stored.
      {:else if entry.kind === MemberHistoryKind.AccessTaken}
        {actor} removed a role or a permission from {member}. The detail is no longer stored.
      {:else if entry.kind === MemberHistoryKind.HistoryLimitChanged}
        {#if entry.historyLimit === MemberHistoryLimit.TwentyFourHours}
          {actor} limited {member} to the last 24 hours.
        {:else if entry.historyLimit === MemberHistoryLimit.Full}
          {actor} gave {member} full history.
        {:else}
          {actor} changed the history limit of {member}. The detail is no longer stored.
        {/if}
      {:else if entry.kind === MemberHistoryKind.AccessEndSet}
        {actor} set {member}'s access to end on {accessEnd}.
      {:else if entry.kind === MemberHistoryKind.AccessEndCleared}
        {actor} removed the end date of {member}'s access.
      {:else if entry.kind === MemberHistoryKind.AccessEndChanged}
        {actor} changed the end date of {member}'s access. The detail is no longer stored.
      {:else if entry.kind === MemberHistoryKind.TeamAccessStopped}
        {#if entry.team?.name}
          {actor} stopped the access of the team {entry.team.name}.
        {:else}
          {actor} stopped the access of a team that was deleted.
        {/if}
      {:else if entry.kind === MemberHistoryKind.TeamAccessGiven}
        {#if entry.team?.name}
          {#if teamRoleCount === 0}
            {actor} gave the team {entry.team.name} access. The detail is no longer stored.
          {:else if teamRoleCount === 1}
            {#if teamLimited && accessEnd}
              {actor} gave the team {entry.team.name} access with the role {teamRoles} to the last 24 hours only, until {accessEnd}.
            {:else if teamLimited}
              {actor} gave the team {entry.team.name} access with the role {teamRoles} to the last 24 hours only.
            {:else if accessEnd}
              {actor} gave the team {entry.team.name} access with the role {teamRoles} until {accessEnd}.
            {:else}
              {actor} gave the team {entry.team.name} access with the role {teamRoles}.
            {/if}
          {:else}
            {#if teamLimited && accessEnd}
              {actor} gave the team {entry.team.name} access with the roles {teamRoles} to the last 24 hours only, until {accessEnd}.
            {:else if teamLimited}
              {actor} gave the team {entry.team.name} access with the roles {teamRoles} to the last 24 hours only.
            {:else if accessEnd}
              {actor} gave the team {entry.team.name} access with the roles {teamRoles} until {accessEnd}.
            {:else}
              {actor} gave the team {entry.team.name} access with the roles {teamRoles}.
            {/if}
          {/if}
        {:else}
          {#if teamRoleCount === 0}
            {actor} gave a team that was deleted access. The detail is no longer stored.
          {:else if teamRoleCount === 1}
            {#if teamLimited && accessEnd}
              {actor} gave a team that was deleted access with the role {teamRoles} to the last 24 hours only, until {accessEnd}.
            {:else if teamLimited}
              {actor} gave a team that was deleted access with the role {teamRoles} to the last 24 hours only.
            {:else if accessEnd}
              {actor} gave a team that was deleted access with the role {teamRoles} until {accessEnd}.
            {:else}
              {actor} gave a team that was deleted access with the role {teamRoles}.
            {/if}
          {:else}
            {#if teamLimited && accessEnd}
              {actor} gave a team that was deleted access with the roles {teamRoles} to the last 24 hours only, until {accessEnd}.
            {:else if teamLimited}
              {actor} gave a team that was deleted access with the roles {teamRoles} to the last 24 hours only.
            {:else if accessEnd}
              {actor} gave a team that was deleted access with the roles {teamRoles} until {accessEnd}.
            {:else}
              {actor} gave a team that was deleted access with the roles {teamRoles}.
            {/if}
          {/if}
        {/if}
      {:else if entry.kind === MemberHistoryKind.TeamAccessChanged}
        {#if entry.team?.name}
          {actor} changed what the team {entry.team.name} can do on this site.
        {:else}
          {actor} changed what a team that was deleted can do on this site.
        {/if}
        {#if teamRoleCount === 0}
          The detail is no longer stored.
        {:else if teamRoleCount === 1}
          {#if teamLimited && accessEnd}
            After this change, the team had access with the role {teamRoles} to the last 24 hours only, until {accessEnd}.
          {:else if teamLimited}
            After this change, the team had access with the role {teamRoles} to the last 24 hours only.
          {:else if accessEnd}
            After this change, the team had access with the role {teamRoles} until {accessEnd}.
          {:else}
            After this change, the team had access with the role {teamRoles}.
          {/if}
        {:else}
          {#if teamLimited && accessEnd}
            After this change, the team had access with the roles {teamRoles} to the last 24 hours only, until {accessEnd}.
          {:else if teamLimited}
            After this change, the team had access with the roles {teamRoles} to the last 24 hours only.
          {:else if accessEnd}
            After this change, the team had access with the roles {teamRoles} until {accessEnd}.
          {:else}
            After this change, the team had access with the roles {teamRoles}.
          {/if}
        {/if}
      {:else if entry.kind === MemberHistoryKind.TeamStaffApproved}
        {#if entry.team?.name}
          {actor} approved {member} as staff of the team {entry.team.name}.
        {:else}
          {actor} approved {member} as staff of a team that was deleted.
        {/if}
      {:else if entry.kind === MemberHistoryKind.TeamStaffDeclined}
        {#if entry.team?.name}
          {actor} declined {member} as staff of the team {entry.team.name}.
        {:else}
          {actor} declined {member} as staff of a team that was deleted.
        {/if}
      {:else if entry.kind === MemberHistoryKind.TeamStaffRuleChanged}
        {#if entry.team?.name && entry.teamManagesStaff === false}
          {actor} turned off "the team manages its own staff" for the team {entry.team.name}. New staff now wait for an owner's approval.
        {:else if entry.team?.name && entry.teamManagesStaff === true}
          {actor} let the team {entry.team.name} approve its own staff.
        {:else if entry.teamManagesStaff === false}
          {actor} turned off "the team manages its own staff" for a team that was deleted.
        {:else if entry.teamManagesStaff === true}
          {actor} let a team that was deleted approve its own staff.
        {:else if entry.team?.name}
          {actor} changed who approves the staff of the team {entry.team.name}. The detail is no longer stored.
        {:else}
          {actor} changed who approves the staff of a team that was deleted. The detail is no longer stored.
        {/if}
      {:else if entry.kind === MemberHistoryKind.RolePermissionsChanged}
        {#if entry.role}
          {actor} changed the permissions of the role {role}.
        {:else}
          {actor} changed the permissions of a role. The detail is no longer stored.
        {/if}
      {/if}
    </p>
    {#if entry.gained?.length}
      <p class="text-muted-foreground">Permissions added: {gained}</p>
    {/if}
    {#if entry.lost?.length}
      <p class="text-muted-foreground">Permissions removed: {lost}</p>
    {/if}
    {#if entry.viaInvite && !entry.inRecordedJoin && entry.kind !== MemberHistoryKind.Joined}
      <p class="text-muted-foreground">
        {#if entry.inviteLabel}
          Through the invite {entry.inviteLabel}.
        {:else}
          Through an invite.
        {/if}
      </p>
    {/if}
  </div>
</li>
