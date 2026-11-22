<script lang="ts">
  import {
    MemberHistoryActorKind,
    MemberHistoryKind,
    MemberHistoryLimit,
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
   * would name them as the giver of their own access.
   */
  const joined = $derived(
    !!entry.viaInvite &&
      !!entry.member?.subjectId &&
      entry.actor?.subjectId === entry.member.subjectId,
  );
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
      {#if joined && entry.kind === MemberHistoryKind.RoleGiven}
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
    {#if entry.viaInvite}
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
