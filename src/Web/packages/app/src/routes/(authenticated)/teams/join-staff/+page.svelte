<script lang="ts">
  import { page } from "$app/state";
  import {
    acceptStaffInvite,
    getStaffInviteInfo,
  } from "#lib/api/generated/teams.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import TeamJoinCard from "#lib/components/teams/TeamJoinCard.svelte";
  import StaffNarrowing from "#lib/components/teams/StaffNarrowing.svelte";

  const token = $derived(page.url.searchParams.get("token") ?? "");
  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const inviteQuery = $derived(token && !refused ? getStaffInviteInfo(token) : undefined);
  retainQuery(() => inviteQuery);
</script>

<svelte:head>
  <title>Join a team as staff - Nocturne</title>
</svelte:head>

<TeamJoinCard
  title="Join a team as staff"
  {refused}
  {token}
  invite={inviteQuery}
  accept={() => acceptStaffInvite(token)}
>
  {#snippet invited(invite)}
    <strong>{invite.createdByName}</strong> invited you to be staff of
    <strong>{invite.teamName}</strong>.
  {/snippet}
  {#snippet details(invite)}
    <div class="space-y-2 text-sm">
      <p>As staff, you see the sites that the team has access to and that let you in. You cannot manage the team.</p>
      <p>
        Your access: <StaffNarrowing narrowing={invite.narrowing} roles={invite.suggestableRoles ?? []} />
      </p>
      <p class="text-muted-foreground">
        At some sites, an owner must approve you before you can see the site.
      </p>
    </div>
  {/snippet}
</TeamJoinCard>
