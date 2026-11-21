<script lang="ts">
  import { page } from "$app/state";
  import { getOverview } from "#lib/api/generated/myTenants.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { plural } from "#lib/utils/plural.js";
  import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
  import Users from "@lucide/svelte/icons/users";
  import PeopleRoster from "./PeopleRoster.svelte";

  /** How often to re-read the cross-tenant overview while the tab is visible. */
  const POLL_MS = 60_000;

  const overviewQuery = getOverview();
  const data = $derived(overviewQuery.current);
  const loadError = $derived(overviewQuery.error);
  const people = $derived(data?.tenants ?? []);

  const baseDomain = $derived(page.data.baseDomain ?? null);

  // A bare await would answer with the value this page already holds.
  pollWhileVisible(() => overviewQuery.refresh(), POLL_MS);
</script>

<svelte:head>
  <title>People - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto space-y-6 p-4 @md:p-6">
  <div class="flex items-center gap-3">
    <div
      class="flex size-12 shrink-0 items-center justify-center rounded-xl bg-primary/10"
    >
      <Users class="size-6 text-primary" aria-hidden="true" />
    </div>
    <div>
      <h1 class="text-2xl font-bold">People</h1>
      <p class="text-sm text-muted-foreground">
        {#if data}
          {plural(people.length, ["# person.", "# people."])}
        {/if}
        Readings refresh every minute.
      </p>
    </div>
  </div>

  {#if loadError}
    <div class="space-y-2">
      <p class="text-sm text-muted-foreground">
        The list of people did not load.
      </p>
      <Button
        variant="outline"
        size="sm"
        onclick={() => overviewQuery.refresh()}
      >
        Try again
      </Button>
    </div>
  {:else if overviewQuery.loading && !data}
    <p class="text-sm text-muted-foreground">Loading…</p>
  {:else if data}
    {#if people.length === 0}
      <EmptyState
        variant="dashed"
        art="people-group"
        title="You follow no other people"
        headingLevel={2}
        body="When a person gives you access to their data, they show here."
      />
    {:else}
      <PeopleRoster {people} {baseDomain} />
    {/if}
  {/if}
</div>
