<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Check from "@lucide/svelte/icons/check";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import UserPlus from "@lucide/svelte/icons/user-plus";
  import { acceptInvite, getInviteInfo } from "#lib/api/generated/teams.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import {
    READ_SURFACE,
    describeRemoteError,
    describeSubmitError,
    errorStatus,
    isSignInRequired,
  } from "#lib/forms/submit-error.js";
  import { MANAGER_KIND } from "#lib/teams/member-kind.svelte.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import TeamsUnavailable from "#lib/components/teams/TeamsUnavailable.svelte";
  import { FormError } from "#lib/forms/index.js";

  const token = $derived(page.url.searchParams.get("token") ?? "");

  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const inviteQuery = $derived(token && !refused ? getInviteInfo(token) : undefined);
  retainQuery(() => inviteQuery);
  const invite = $derived(inviteQuery?.current);

  const unusableReason = $derived.by(() => {
    if (!token) return "This link has no invite token. Check the link that you were given.";
    const err = inviteQuery?.error;
    if (!err) return null;
    if (errorStatus(err) === 404) {
      return "This invite link is not valid. Ask the manager who sent it for a new link.";
    }
    if (errorStatus(err) === 403 && !isSignInRequired(err)) return "Your sign-in cannot join a team.";
    return describeRemoteError(err, "This invite link cannot be used.", READ_SURFACE, teamProblemMessages());
  });

  let accepting = $state(false);
  let acceptError = $state<string | null>(null);

  async function accept() {
    accepting = true;
    acceptError = null;
    try {
      const accepted = await acceptInvite(token);
      await goto(resolve("/(authenticated)/settings/teams/[teamId]", { teamId: accepted.teamId ?? "" }));
    } catch (err) {
      acceptError = describeSubmitError(
        err,
        "Could not accept the invite. Try again.",
        teamProblemMessages(),
      );
    } finally {
      accepting = false;
    }
  }
</script>

<svelte:head>
  <title>Join a team - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    {#if refused}
      <Card.Header class="space-y-1 text-center">
        <Card.Title class="text-2xl font-bold">Join a team</Card.Title>
        <Card.Description><TeamsUnavailable /></Card.Description>
      </Card.Header>
    {:else if accepting}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <Loader2 class="h-6 w-6 animate-spin text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Joining...</Card.Title>
      </Card.Header>
    {:else if unusableReason}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10">
          <AlertTriangle class="h-6 w-6 text-destructive" />
        </div>
        <Card.Title class="text-2xl font-bold">Invite unavailable</Card.Title>
        <Card.Description>{unusableReason}</Card.Description>
      </Card.Header>
    {:else if !invite}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <Loader2 class="h-6 w-6 animate-spin text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Loading invite...</Card.Title>
      </Card.Header>
    {:else if invite.alreadyMember}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <Check class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">You are already a member</Card.Title>
        <Card.Description>
          You are already a member of <strong>{invite.teamName}</strong>.
        </Card.Description>
      </Card.Header>
      <Card.Content>
        <Button
          class="w-full"
          size="lg"
          href={resolve("/(authenticated)/settings/teams/[teamId]", { teamId: invite.teamId ?? "" })}
        >
          Open the team
        </Button>
      </Card.Content>
    {:else}
      <Card.Header class="space-y-1 text-center">
        <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <UserPlus class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Join a team</Card.Title>
        <Card.Description>
          {#if invite.kind === MANAGER_KIND}
            <strong>{invite.createdByName}</strong> invited you to be a manager of
            <strong>{invite.teamName}</strong>.
          {:else}
            <strong>{invite.createdByName}</strong> invited you to join
            <strong>{invite.teamName}</strong>.
          {/if}
        </Card.Description>
      </Card.Header>
      <Card.Content class="space-y-4">
        <FormError issues={acceptError} focusOnShow />
        <Button class="w-full" size="lg" onclick={accept}>
          <UserPlus class="mr-2 h-5 w-5" />
          Accept invite
        </Button>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
