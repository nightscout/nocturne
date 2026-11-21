<script lang="ts">
  import { afterNavigate, replaceState } from "$app/navigation";
  import { page } from "$app/state";
  import {
    getOverview,
    getReview,
  } from "#lib/api/generated/myTenants.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { formatMediumDateRange } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
  import Users from "@lucide/svelte/icons/users";
  import PeopleRoster from "./PeopleRoster.svelte";
  import {
    REVIEW_DAYS,
    lensUrl,
    parseDays,
    parseLens,
    type Lens,
    type ReviewDays,
    type ReviewState,
  } from "./review-lens.js";

  /** How often to re-read the cross-tenant overview while the tab is visible. */
  const POLL_MS = 60_000;

  const overviewQuery = getOverview();
  const data = $derived(overviewQuery.current);
  const loadError = $derived(overviewQuery.error);
  const people = $derived(data?.tenants ?? []);

  const baseDomain = $derived(page.data.baseDomain ?? null);

  let lens = $state<Lens>(parseLens(page.url.searchParams.get("lens")));
  let days = $state<ReviewDays>(parseDays(page.url.searchParams.get("days")));

  // A navigation to this same route reuses the page: the URL it lands on names the lens.
  afterNavigate(() => {
    lens = parseLens(page.url.searchParams.get("lens"));
    days = parseDays(page.url.searchParams.get("days"));
  });

  function show(nextLens: Lens, nextDays: ReviewDays) {
    lens = nextLens;
    days = nextDays;
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its query changed, already resolved
    replaceState(lensUrl(page.url, nextLens, nextDays), {});
  }

  // The review read is not polled: the API caches it, and a period does not move by the minute.
  const reviewQuery = $derived(lens === "review" ? getReview({ days }) : null);
  const reviewData = $derived(reviewQuery?.current);
  const reviewFailed = $derived(!!reviewQuery?.error);
  const review = $derived<ReviewState | undefined>(
    lens === "now"
      ? undefined
      : reviewFailed
        ? { status: "failed" }
        : reviewData
          ? { status: "ready", response: reviewData, days }
          : { status: "loading" }
  );

  // A bare await would answer with the value this page already holds.
  pollWhileVisible(() => overviewQuery.refresh(), POLL_MS);
</script>

<svelte:head>
  <title>People - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto space-y-6 p-4 @md:p-6">
  <div class="flex flex-wrap items-center gap-x-3 gap-y-3">
    <div
      class="flex size-12 shrink-0 items-center justify-center rounded-xl bg-primary/10"
    >
      <Users class="size-6 text-primary" aria-hidden="true" />
    </div>
    <div class="min-w-0 flex-1">
      <h1 class="text-2xl font-bold">People</h1>
      <p class="text-sm text-muted-foreground">
        {#if data}
          {plural(people.length, ["# person.", "# people."])}
        {/if}
        {#if lens === "review"}
          {#if reviewData?.from && reviewData.to}
            Statistics for {formatMediumDateRange(
              new Date(reviewData.from),
              new Date(reviewData.to)
            )}.
          {/if}
        {:else}
          Readings refresh every minute.
        {/if}
      </p>
    </div>
    <div
      class="flex w-full flex-col gap-2 @2xl:ml-auto @2xl:w-auto @2xl:flex-row @2xl:items-center"
    >
      <ToggleGroup.Root
        type="single"
        value={lens}
        onValueChange={(value) => value && show(parseLens(value), days)}
        variant="segmented"
        size="sm"
        aria-label="View"
        class="w-full @2xl:w-fit"
      >
        <ToggleGroup.Item value="now" class="flex-1 @2xl:flex-none">Now</ToggleGroup.Item>
        <ToggleGroup.Item value="review" class="flex-1 @2xl:flex-none">Review</ToggleGroup.Item>
      </ToggleGroup.Root>
      {#if lens === "review"}
        <ToggleGroup.Root
          type="single"
          value={String(days)}
          onValueChange={(value) => value && show(lens, parseDays(value))}
          variant="segmented"
          size="sm"
          aria-label="Period"
          class="w-full @2xl:w-fit"
        >
          {#each REVIEW_DAYS as option (option)}
            <ToggleGroup.Item value={String(option)} class="flex-1 @2xl:flex-none">
              {plural(option, ["# day", "# days"])}
            </ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      {/if}
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
      {#if review?.status === "failed"}
        <div class="flex flex-wrap items-center gap-3">
          <p class="text-sm text-muted-foreground">
            The statistics did not load.
          </p>
          <Button
            variant="outline"
            size="sm"
            onclick={() => reviewQuery?.refresh()}
          >
            Try again
          </Button>
        </div>
      {/if}
      <PeopleRoster {people} {baseDomain} {review} />
    {/if}
  {/if}
</div>
