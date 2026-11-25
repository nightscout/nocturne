<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import UsersRound from "@lucide/svelte/icons/users-round";
  import { createTeam, getMyTeams } from "#lib/api/generated/teams.generated.remote.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { teamNameMaxLength } from "#lib/teams/limits.js";
  import { memberKindLabel } from "#lib/teams/member-kind.svelte.js";
  import { teamProblemMessages } from "#lib/teams/problem-messages.svelte.js";
  import TeamsUnavailable from "#lib/components/teams/TeamsUnavailable.svelte";

  const refused = $derived(page.data.refusedAsDemoSubject === true);
  const teamsQuery = $derived(refused ? null : getMyTeams());
  retainQuery(() => teamsQuery);
  const myTeams = $derived(teamsQuery?.current);

  let name = $state("");
  let creating = $state(false);
  let createError = $state<string | null>(null);

  async function create() {
    const trimmed = name.trim();
    if (!trimmed || creating) return;
    creating = true;
    createError = null;
    try {
      const team = await createTeam({ name: trimmed });
      name = "";
      await goto(resolve("/(authenticated)/settings/teams/[teamId]", { teamId: team.id ?? "" }));
    } catch (err) {
      createError = describeSubmitError(
        err,
        "Could not create the team. Try again.",
        teamProblemMessages(),
      );
    } finally {
      creating = false;
    }
  }
</script>

<svelte:head>
  <title>Teams - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl space-y-6 p-3 @md:p-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <UsersRound class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Teams</h1>
      <p class="text-muted-foreground">The teams that you are a member of.</p>
    </div>
  </div>

  {#if refused}
    <TeamsUnavailable />
  {:else if teamsQuery?.error}
    <Alert.Root variant="destructive">
      <AlertTriangle class="h-4 w-4" />
      <Alert.Title>Could not load your teams</Alert.Title>
      <Alert.Description>
        {remoteErrorMessage(teamsQuery?.error, "Reload the page to try again.")}
      </Alert.Description>
    </Alert.Root>
  {:else if !myTeams}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else}
    <Card.Root>
      <Card.Header>
        <Card.Title>Your teams</Card.Title>
      </Card.Header>
      <Card.Content>
        {#if (myTeams.items ?? []).length === 0}
          <p class="text-sm text-muted-foreground">You are not a member of a team.</p>
        {:else}
          <ul class="divide-y">
            {#each myTeams.items ?? [] as team (team.id)}
              <li>
                <a
                  class="-mx-2 flex items-center justify-between gap-3 rounded-md px-2 py-3 hover:bg-accent/50"
                  href={resolve("/(authenticated)/settings/teams/[teamId]", { teamId: team.id ?? "" })}
                >
                  <span class="flex min-w-0 items-center gap-2">
                    <span class="truncate font-medium">{team.name}</span>
                    <Badge variant="secondary">{memberKindLabel(team.kind)}</Badge>
                  </span>
                  <ChevronRight class="h-4 w-4 shrink-0 text-muted-foreground" />
                </a>
              </li>
            {/each}
          </ul>
        {/if}
      </Card.Content>
    </Card.Root>

    {#if myTeams.canCreate}
      <Card.Root>
        <Card.Header>
          <Card.Title>Create a team</Card.Title>
          <Card.Description>You are the first manager of a team that you create.</Card.Description>
        </Card.Header>
        <Card.Content>
          <form
            class="space-y-4"
            onsubmit={(e) => {
              e.preventDefault();
              void create();
            }}
          >
            <div class="space-y-2">
              <Label for="new-team-name">Team name</Label>
              <Input
                id="new-team-name"
                bind:value={name}
                maxlength={teamNameMaxLength}
                autocomplete="off"
              />
            </div>
            {#if createError}
              <p class="text-sm text-destructive" role="alert">{createError}</p>
            {/if}
            <Button type="submit" disabled={creating || name.trim() === ""}>
              {#if creating}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Create team
            </Button>
          </form>
        </Card.Content>
      </Card.Root>
    {:else}
      <p class="text-sm text-muted-foreground">
        The operator of this site limits who can create a team.
      </p>
    {/if}
  {/if}
</div>
