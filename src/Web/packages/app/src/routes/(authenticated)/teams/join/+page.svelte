<script lang="ts">
  import { page } from "$app/state";
  import { acceptInvite, getInviteInfo } from "#lib/api/generated/teams.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { MANAGER_KIND } from "#lib/teams/member-kind.svelte.js";
  import TeamJoinCard from "#lib/components/teams/TeamJoinCard.svelte";

  const token = $derived(page.url.searchParams.get("token") ?? "");
  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const inviteQuery = $derived(token && !refused ? getInviteInfo(token) : undefined);
  retainQuery(() => inviteQuery);
</script>

<svelte:head>
  <title>Join a team - Nocturne</title>
</svelte:head>

<TeamJoinCard
  title="Join a team"
  {refused}
  {token}
  invite={inviteQuery}
  accept={() => acceptInvite(token)}
>
  {#snippet invited(invite)}
    {#if invite.kind === MANAGER_KIND}
      <strong>{invite.createdByName}</strong> invited you to be a manager of
      <strong>{invite.teamName}</strong>.
    {:else}
      <strong>{invite.createdByName}</strong> invited you to join
      <strong>{invite.teamName}</strong>.
    {/if}
  {/snippet}
</TeamJoinCard>
