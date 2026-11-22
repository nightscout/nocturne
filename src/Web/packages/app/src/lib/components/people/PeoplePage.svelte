<script lang="ts">
  import { afterNavigate, replaceState } from "$app/navigation";
  import { tick, untrack } from "svelte";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { getReview } from "#lib/api/generated/myTenants.generated.remote.js";
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import * as Sheet from "#lib/components/ui/sheet/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { formatMediumDateRange } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import Monitor from "@lucide/svelte/icons/monitor";
  import Users from "@lucide/svelte/icons/users";
  import PeopleRoster from "./PeopleRoster.svelte";
  import { followPeople } from "./people-overview.svelte.js";
  import { setReadState } from "./read-state.js";
  import PersonPanel from "./PersonPanel.svelte";
  import { PERSON_PANEL_HEADING_ID } from "./person.js";
  import {
    REVIEW_DAYS,
    pageUrl,
    parseDays,
    parseLens,
    type Lens,
    type PageView,
    type ReviewDays,
    type ReviewState,
  } from "./review-lens.js";

  /** The content width of Tailwind's @4xl container, from which the panel sits beside the list. */
  const SIDE_BY_SIDE_MIN_PX = 896;

  const overview = followPeople();
  const overviewQuery = overview.query;
  const clock = new Now();
  setReadState({
    get disconnected() {
      return !!overviewQuery.error;
    },
    get now() {
      return clock.current;
    },
  });
  const data = $derived(overviewQuery.current);
  const loadError = $derived(overviewQuery.error);
  const people = $derived(data?.tenants ?? []);

  const baseDomain = $derived(page.data.baseDomain ?? null);

  let lens = $state<Lens>(parseLens(page.url.searchParams.get("lens")));
  let days = $state<ReviewDays>(parseDays(page.url.searchParams.get("days")));
  let personSlug = $state(page.url.searchParams.get("person"));
  let contentWidth = $state(0);
  let heading = $state<HTMLElement>();
  let lastOpen: string | null = null;
  let roster = $state<ReturnType<typeof PeopleRoster>>();

  // A navigation to this same route reuses the page: the address it lands on names the lens. Back
  // to an entry written by replaceState lands on the entry's old page.url, and the address shown is
  // page.shallow.url.
  afterNavigate(() => {
    const { searchParams } = page.shallow?.url ?? page.url;
    lens = parseLens(searchParams.get("lens"));
    days = parseDays(searchParams.get("days"));
    personSlug = searchParams.get("person");
  });

  // page.url keeps the address of the last navigation, not of a replaceState, so the address is
  // built from what the page holds.
  function addressOf(view: Partial<PageView> = {}) {
    return pageUrl(page.url, { lens, days, person: personSlug, ...view });
  }

  function show(nextLens: Lens, nextDays: ReviewDays) {
    lens = nextLens;
    days = nextDays;
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its query changed, already resolved
    replaceState(addressOf(), {});
  }

  function open(slug: string | null) {
    personSlug = slug;
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its query changed, already resolved
    replaceState(addressOf(), {});
  }

  const selected = $derived(people.find((person) => person.slug === personSlug));
  // Unmeasured, the page has not chosen a placement yet.
  const placement = $derived(
    contentWidth === 0 ? null : contentWidth >= SIDE_BY_SIDE_MIN_PX ? "side" : "sheet"
  );

  // A person who leaves the list, as access ends, takes the open panel with them.
  $effect(() => {
    if (!data || !personSlug) return;
    if (selected) {
      lastOpen = personSlug;
      return;
    }
    const wasOpen = lastOpen === personSlug;
    untrack(() => {
      lastOpen = null;
      open(null);
      if (wasOpen && (!document.activeElement || document.activeElement === document.body)) {
        heading?.focus();
      }
    });
  });

  async function closePanel() {
    const slug = personSlug;
    open(null);
    await tick();
    if (slug) roster?.focusRow(slug);
  }

  function personLocation(person: TenantOverviewItem) {
    const url = addressOf({ person: person.slug ?? null });
    return url.pathname + url.search;
  }

  function select(person: TenantOverviewItem) {
    if (person.slug) open(person.slug);
  }

  const rereads = $state<Partial<Record<ReviewDays, number>>>({});
  const refreshing = $derived((rereads[days] ?? 0) > 0);
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
          ? { status: "ready", response: reviewData, days, refreshing }
          : { status: "loading" }
  );

  // The review read is not polled, so it ages behind the list: a person who gave access since
  // is missing from it. Each person is read for once per period; the API may still omit them.
  const missingIds = $derived.by(() => {
    if (!reviewData) return [];
    const reviewed = new Set((reviewData.people ?? []).map((entry) => entry.tenantId));
    return people.flatMap((person) =>
      person.tenantId && !reviewed.has(person.tenantId) ? [person.tenantId] : []
    );
  });
  const rereadIds: Partial<Record<ReviewDays, Set<string>>> = {};

  $effect(() => {
    const period = days;
    const missing = missingIds;
    const done = rereadIds[period] ?? new Set<string>();
    if (missing.every((id) => done.has(id))) return;
    rereadIds[period] = new Set([...done, ...missing]);
    untrack(() => {
      rereads[period] = (rereads[period] ?? 0) + 1;
      // A failed read shows through the query's error.
      void reviewQuery
        ?.refresh()
        .catch(() => {})
        .finally(() => (rereads[period] = (rereads[period] ?? 1) - 1));
    });
  });
</script>

<svelte:head>
  <title>People - Nocturne</title>
</svelte:head>

<div
  class="@container container mx-auto space-y-6 p-4 @md:p-6"
>
  <div class="flex flex-wrap items-center gap-x-3 gap-y-3">
    <div
      class="flex size-12 shrink-0 items-center justify-center rounded-xl bg-primary/10"
    >
      <Users class="size-6 text-primary" aria-hidden="true" />
    </div>
    <div class="min-w-0 flex-1">
      <h1 bind:this={heading} tabindex="-1" class="text-2xl font-bold outline-none">
          People
        </h1>
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
        {:else if overview.live}
          Readings update as they arrive.
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
      {#if lens === "now"}
        <Button
          variant="outline"
          size="sm"
          href={resolve("/(authenticated)/tenants/wall")}
        >
          <Monitor class="size-4" aria-hidden="true" />
          Wall display
        </Button>
      {:else}
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
      <div class="flex items-start gap-6" bind:clientWidth={contentWidth}>
        <div class="min-w-0 flex-1">
          <PeopleRoster
            bind:this={roster}
            {people}
            {review}
            selectedSlug={selected?.slug ?? null}
            onSelect={select}
            hrefFor={personLocation}
          />
        </div>
        {#if selected && placement === "side"}
          <aside
            class="sticky top-[calc(var(--app-sticky-top,0px)+1rem)] max-h-[calc(100dvh-var(--app-sticky-top,0px)-2rem)] w-96 shrink-0 self-start overflow-y-auto border-l"
            aria-labelledby={PERSON_PANEL_HEADING_ID}
          >
            <PersonPanel
              person={selected}
              {baseDomain}
              placement="side"
              onClose={closePanel}
            />
          </aside>
        {/if}
      </div>
      {#if selected && placement === "sheet"}
        <Sheet.Root open onOpenChange={(next) => !next && closePanel()}>
          <Sheet.Content
            side="right"
            showClose={false}
            class="w-full gap-0 overflow-y-auto sm:max-w-sm"
            aria-describedby={undefined}
            onCloseAutoFocus={(event) => event.preventDefault()}
          >
            <PersonPanel
              person={selected}
              {baseDomain}
              placement="sheet"
              onClose={closePanel}
            />
          </Sheet.Content>
        </Sheet.Root>
      {/if}
    {/if}
  {/if}
</div>
