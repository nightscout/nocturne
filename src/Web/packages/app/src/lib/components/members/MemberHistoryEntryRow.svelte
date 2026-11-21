<script lang="ts">
  import {
    MemberHistoryActorKind,
    MemberHistoryKind,
    MemberHistoryLimit,
    type MemberHistoryActor,
    type MemberHistoryEntry,
    type TenantRoleDto,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { scopeLabels } from "#lib/constants/scope-labels.svelte.js";
  import { formatDateTime } from "#lib/utils/formatting.js";

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
      {#if entry.kind === MemberHistoryKind.RoleGiven}
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
