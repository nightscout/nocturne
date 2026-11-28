<script lang="ts">
  import { afterNavigate, replaceState } from "$app/navigation";
  import { tick, untrack } from "svelte";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { isHttpError } from "@sveltejs/kit";
  import { getGroups, getReview } from "#lib/api/generated/myTenants.generated.remote.js";
  import {
    createPeopleView,
    getPeopleViews,
    updatePeopleView,
  } from "#lib/api/generated/peopleViews.generated.remote.js";
  import {
    OverviewRuleSet,
    type PeopleFilter,
    type PeopleSortColumn,
    type TenantOverviewItem,
    type TenantOverviewResponse,
    type TenantReviewResponse,
  } from "@nocturne/api-client";
  import { demoSubjectAccess } from "#lib/authorization/demo-subject.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import * as Sheet from "#lib/components/ui/sheet/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { formatMediumDateRange } from "#lib/utils/formatting.js";
  import { plural } from "#lib/utils/plural.js";
  import Flag from "@lucide/svelte/icons/flag";
  import Monitor from "@lucide/svelte/icons/monitor";
  import Settings2 from "@lucide/svelte/icons/settings-2";
  import Users from "@lucide/svelte/icons/users";
  import HiddenInGroup from "./HiddenInGroup.svelte";
  import PeopleFilters from "./PeopleFilters.svelte";
  import PeopleRoster from "./PeopleRoster.svelte";
  import PeopleSearch from "./PeopleSearch.svelte";
  import PeopleTabs from "./PeopleTabs.svelte";
  import PeopleViewsManager from "./PeopleViewsManager.svelte";
  import TabFallback from "./TabFallback.svelte";
  import {
    EVERYONE,
    fallbackOf,
    groupOf,
    tabOf,
    tabValue,
    viewOf,
    type Fallback,
    type PeopleTab,
  } from "./people-tab.js";
  import {
    EMPTY_QUERY,
    PLAIN,
    hasFactFilter,
    hasUnsaved,
    isEmptyQuery,
    isReviewSort,
    isWorthKeeping,
    nextSort,
    pageUrl,
    readUnsaved,
    reviewRequest,
    shownOf,
    shownOfView,
    unsavedOf,
    viewRequest,
    withTabQuery,
    withLens,
    type PageAddress,
    type Shown,
    type Unsaved,
  } from "./people-query.js";
  import { RULES_PENDING_REREAD_MS, followPeople } from "./people-overview.svelte.js";
  import { VIEW_NAME_MAX_LENGTH, isNameTaken, viewProblemMessages } from "./people-views.svelte.js";
  import { setReadState } from "./read-state.js";
  import PersonPanel from "./PersonPanel.svelte";
  import { PERSON_PANEL_HEADING_ID, keepUnchanged } from "./person.js";
  import {
    DEFAULT_REVIEW_DAYS,
    REVIEW_DAYS,
    inReviewOrder,
    parseDays,
    parseLens,
    type Lens,
    type ReviewDays,
    type ReviewState,
  } from "./review-lens.js";

  /** The content width of Tailwind's @4xl container, from which the panel sits beside the list. */
  const SIDE_BY_SIDE_MIN_PX = 896;

  // Back to an entry written by replaceState lands on the entry's old page.url; the address shown is
  // page.shallow.url.
  const address = () => (page.shallow?.url ?? page.url).searchParams;

  let tab = $state<PeopleTab>(tabOf(address()));
  /** What the viewer changed on the tab and has not saved, as the address holds it. */
  let unsaved = $state<Unsaved>(readUnsaved(address()));
  /** Why the page shows everyone in place of the group or view the address named. */
  let fellBack = $state<Fallback | null>(null);
  /** How many reads of the groups, and of the views, are running after a tab named one they did not list. */
  let groupRereads = $state(0);
  let viewRereads = $state(0);
  const rereadingGroups = $derived(groupRereads > 0);
  const rereadingViews = $derived(viewRereads > 0);
  const group = $derived(groupOf(tab));

  const groupsQuery = getGroups();
  const groups = $derived(groupsQuery.current ?? []);
  const viewsQuery = getPeopleViews();
  const views = $derived(viewsQuery.current?.views ?? []);
  const viewLimit = $derived(viewsQuery.current?.limit ?? 0);
  const atViewLimit = $derived(!!viewsQuery.current && views.length >= viewLimit);
  const viewId = $derived(viewOf(tab));
  const openView = $derived(viewId ? views.find((summary) => summary.view.id === viewId) : undefined);
  /** What the tab shows before any change: a view's saved query and lens. Null while a view is not read. */
  const own = $derived<Shown | null>(
    tab.kind === "view" ? (openView ? shownOfView(openView.view) : null) : PLAIN
  );
  const shown = $derived(own ? shownOf(own, unsaved) : null);
  const changes = $derived(own && shown ? unsavedOf(own, shown) : unsaved);
  const query = $derived(shown?.query ?? EMPTY_QUERY);
  const lens = $derived<Lens>(shown?.lens ?? "now");
  const days = $derived<ReviewDays>(shown?.days ?? DEFAULT_REVIEW_DAYS);

  const overview = followPeople(
    () => (shown ? { tab, query: shown.query } : null),
    () => {
      void groupsQuery.refresh().catch(() => {});
      void viewsQuery.refresh().catch(() => {});
    },
    OverviewRuleSet.All
  );
  const overviewQuery = $derived(overview.query);
  const clock = new Now();
  /** The group's own read answers that the viewer is not in it. */
  const groupNotFound = $derived(
    isHttpError(overviewQuery?.error) && overviewQuery.error.status === 404
  );
  // While the groups are read again, the group's 404 waits on them: the page then shows the group or
  // falls back to everyone, and neither is a failed read.
  const loadError = $derived(
    groupNotFound && rereadingGroups ? undefined : overviewQuery?.error
  );
  setReadState({
    get disconnected() {
      return !!loadError;
    },
    get now() {
      return clock.current;
    },
  });
  // Each query is a read of its own, with no value until it answers. The tab's last answer stays on
  // screen meanwhile, so a change of search, filter or sort keeps the list and the control in use.
  const freshData = $derived(overviewQuery?.current);
  let lastData = $state.raw<{ tab: string; response: TenantOverviewResponse } | null>(null);
  $effect.pre(() => {
    const current = freshData;
    if (current) lastData = { tab: tabValue(untrack(() => tab)), response: current };
  });
  const data = $derived(
    freshData ?? (lastData?.tab === tabValue(tab) ? lastData.response : undefined)
  );
  let heldPeople: TenantOverviewItem[] = [];
  const people = $derived.by(() => {
    heldPeople = keepUnchanged(heldPeople, data?.tenants ?? []);
    return heldPeople;
  });
  const hiddenCount = $derived(data?.hiddenCount ?? 0);

  const baseDomain = $derived(page.data.baseDomain ?? null);

  let personSlug = $state(address().get("person"));
  let contentWidth = $state(0);
  let heading = $state<HTMLElement>();
  const headingId = $props.id();
  let lastOpen: string | null = null;
  let roster = $state<ReturnType<typeof PeopleRoster>>();

  /** Reads the groups or the views again when `next` names one they do not list. */
  function rereadUnlisted(next: PeopleTab) {
    const groupId = groupOf(next);
    const listedGroups = groupsQuery.current;
    if (groupId && listedGroups && !listedGroups.some((listed) => listed.id === groupId)) {
      groupRereads += 1;
      void groupsQuery
        .refresh()
        .catch(() => {})
        .finally(() => (groupRereads -= 1));
    }
    const viewId = viewOf(next);
    const listedViews = viewsQuery.current?.views;
    if (viewId && listedViews && !listedViews.some((listed) => listed.view.id === viewId)) {
      viewRereads += 1;
      void viewsQuery
        .refresh()
        .catch(() => {})
        .finally(() => (viewRereads -= 1));
    }
  }

  // A navigation to this same route reuses the page: the address it lands on names the tab.
  afterNavigate(() => {
    const searchParams = address();
    tab = tabOf(searchParams);
    unsaved = readUnsaved(searchParams);
    fellBack = null;
    rereadUnlisted(tab);
    personSlug = searchParams.get("person");
  });

  // page.url keeps the address of the last navigation, not of a replaceState, so the address is
  // built from what the page holds.
  function addressOf(next: Partial<PageAddress> = {}) {
    return pageUrl(page.url, { tab, unsaved: changes, person: personSlug, ...next });
  }

  function writeAddress() {
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its query changed, already resolved
    replaceState(addressOf(), {});
  }

  const wallLocation = $derived.by(() => {
    const url = withTabQuery(new URL(resolve("/(authenticated)/people/wall"), page.url.href), tab, changes);
    return url.pathname + url.search;
  });

  /** Opens `next` as saved. Everyone and a group keep the lens and period the page shows. */
  function choose(next: PeopleTab) {
    const carried: Unsaved =
      next.kind !== "view" && lens === "review" ? { lens: "review", days } : {};
    tab = next;
    unsaved = carried;
    fellBack = null;
    rereadUnlisted(next);
    writeAddress();
  }

  function fallBack(fallback: Fallback) {
    choose(EVERYONE);
    fellBack = fallback;
  }

  // The lists the API gives, or a group's 404 for its own read, say whether the viewer has the tab the
  // address named. Without its list the page has no tab to show it under, so it shows everyone.
  $effect(() => {
    if (tab.kind === "group" && !rereadingGroups) {
      const reason = fallbackOf(tab.id, groupsQuery.current, !!groupsQuery.error, groupNotFound);
      if (reason) untrack(() => fallBack({ kind: "group", reason }));
    }
    if (tab.kind === "view" && !rereadingViews && tab.id !== deletingView) {
      const listed = viewsQuery.current?.views.map((summary) => summary.view);
      const reason = fallbackOf(tab.id, listed, !!viewsQuery.error, false);
      if (reason) untrack(() => fallBack({ kind: "view", reason }));
    }
  });

  /** Shows `next` on the open tab, as a change not yet saved. */
  function change(next: Shown) {
    if (!own) return;
    unsaved = unsavedOf(own, next);
    writeAddress();
  }

  function show(nextLens: Lens, nextDays: ReviewDays) {
    if (shown) change(withLens(shown, nextLens, nextDays));
  }

  /** The period last shown in the Review lens, which the Now lens does not hold, for a return to Review. */
  let lastReviewDays: ReviewDays | null = null;

  function showLens(nextLens: Lens) {
    if (!shown) return;
    if (shown.lens === "review") lastReviewDays = shown.days;
    show(nextLens, nextLens === "review" ? (lastReviewDays ?? shown.days) : shown.days);
  }

  function search(text: string) {
    if (shown) change({ ...shown, query: { ...shown.query, search: text } });
  }

  function filterBy(filters: PeopleFilter[]) {
    if (shown) change({ ...shown, query: { ...shown.query, filters } });
  }

  function sortBy(column: PeopleSortColumn) {
    if (shown) {
      change({ ...shown, query: { ...shown.query, sort: nextSort(shown.query.sort, column) } });
    }
  }

  function open(slug: string | null) {
    personSlug = slug;
    writeAddress();
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

  // The review read is not polled: the API caches it, and a period does not move by the minute.
  const reviewArgs = $derived(
    lens === "review" && shown ? reviewRequest(tab, shown.query, OverviewRuleSet.All, days) : null
  );
  /** The review read of one tab, query and period. */
  const reviewKey = $derived(JSON.stringify(reviewArgs));
  const rereads = $state<Record<string, number>>({});
  const refreshing = $derived((rereads[reviewKey] ?? 0) > 0);
  const reviewQuery = $derived(reviewArgs ? getReview(reviewArgs) : null);
  const freshReview = $derived(reviewQuery?.current);
  // One period's figures never show under another, so the review is kept only within a tab and period.
  const reviewPlace = $derived(`${tabValue(tab)} ${days}`);
  let lastReview = $state.raw<{ place: string; response: TenantReviewResponse } | null>(null);
  $effect.pre(() => {
    const current = freshReview;
    if (current) lastReview = { place: untrack(() => reviewPlace), response: current };
  });
  const keptReview = $derived(
    !freshReview && lastReview?.place === reviewPlace ? lastReview : null
  );
  const reviewData = $derived(freshReview ?? keptReview?.response);
  const reviewFailed = $derived(!!reviewQuery?.error);
  const review = $derived<ReviewState | undefined>(
    lens === "now"
      ? undefined
      : reviewFailed
        ? { status: "failed" }
        : reviewData
          ? {
              status: "ready",
              response: reviewData,
              days,
              refreshing: refreshing || !!keptReview,
            }
          : { status: "loading" }
  );
  /**
   * The rows of the fresh answers to the current query: the overview, and under a review sort the review
   * that orders it. Null while either is still the kept answer of an earlier query.
   */
  const freshRows = $derived(
    !freshData
      ? null
      : !isReviewSort(query.sort) || reviewFailed
        ? people
        : freshReview
          ? inReviewOrder(people, freshReview)
          : null
  );
  let lastRows = $state.raw<TenantOverviewItem[] | null>(null);
  $effect.pre(() => {
    const current = freshRows;
    if (current) lastRows = current;
  });
  /** The list shows a kept answer: it keeps its rows in the order they had, and states nothing it counted. */
  const stale = $derived(!!data && !freshRows);
  const rows = $derived(freshRows ?? lastRows ?? people);
  /** People still being checked for a data filter, whom the list leaves out until they are. */
  const stillChecking = $derived(
    hasFactFilter(query) && !stale
      ? ((lens === "review" ? freshReview : freshData)?.peopleWithRulesPending ?? 0)
      : 0
  );

  $effect(() => {
    const read = reviewQuery;
    if (!read || (freshReview?.peopleWithRulesPending ?? 0) <= 0) return;
    // A failed read shows through the query's error.
    const reread = setTimeout(() => void read.refresh().catch(() => {}), RULES_PENDING_REREAD_MS);
    return () => clearTimeout(reread);
  });

  // The review read is not polled, so it ages behind the list: a person who gave access since
  // is missing from it. Each person is read for once per period; the API may still omit them.
  const missingIds = $derived.by(() => {
    if (!freshReview) return [];
    const reviewed = new Set((freshReview.people ?? []).map((entry) => entry.tenantId));
    return people.flatMap((person) =>
      person.tenantId && !reviewed.has(person.tenantId) ? [person.tenantId] : []
    );
  });
  const rereadIds: Record<string, Set<string>> = {};

  $effect(() => {
    const key = reviewKey;
    const missing = missingIds;
    const done = rereadIds[key] ?? new Set<string>();
    if (missing.every((id) => done.has(id))) return;
    rereadIds[key] = new Set([...done, ...missing]);
    untrack(() => {
      rereads[key] = (rereads[key] ?? 0) + 1;
      // A failed read shows through the query's error.
      void reviewQuery
        ?.refresh()
        .catch(() => {})
        .finally(() => (rereads[key] = (rereads[key] ?? 1) - 1));
    });
  });

  const canKeepViews = $derived(demoSubjectAccess(page.data).canWriteStaffRecords);
  const canSaveView = $derived(!!openView && hasUnsaved(changes));
  const worthKeeping = $derived(!!shown && isWorthKeeping(shown));
  let managing = $state(false);
  let deletingView = $state<string | null>(null);
  let naming = $state(false);
  let newName = $state("");
  let saving = $state(false);
  let saveError = $state<string | null>(null);
  let nameError = $state<string | null>(null);
  let nameTaken = $state(false);

  function startNaming() {
    newName = "";
    nameError = null;
    nameTaken = false;
    naming = true;
  }

  async function saveAsNew(event: SubmitEvent) {
    event.preventDefault();
    if (!shown || saving || newName.trim() === "") return;
    saving = true;
    nameError = null;
    nameTaken = false;
    try {
      const created = await createPeopleView(viewRequest(newName, shown));
      naming = false;
      if (created.id) choose({ kind: "view", id: created.id });
    } catch (err) {
      nameTaken = isNameTaken(err);
      nameError = describeSubmitError(
        err,
        "Could not save the view. Try again.",
        viewProblemMessages(viewLimit)
      );
    } finally {
      saving = false;
    }
  }

  async function saveView() {
    const view = openView?.view;
    if (!view?.id || !shown || saving) return;
    saving = true;
    saveError = null;
    try {
      await updatePeopleView({ viewId: view.id, request: viewRequest(view.name ?? "", shown) });
      // What changed while the save ran stays a change over the saved view.
      if (viewOf(tab) === view.id) writeAddress();
    } catch (err) {
      saveError = describeSubmitError(
        err,
        "Could not save the view. Try again.",
        viewProblemMessages(viewLimit)
      );
    } finally {
      saving = false;
    }
  }

  function deleted(viewId: string, gone: boolean) {
    if (gone && viewOf(tab) === viewId) choose(EVERYONE);
    deletingView = null;
  }
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
      <h1 id={headingId} bind:this={heading} tabindex="-1" class="text-2xl font-bold outline-none">
          People
        </h1>
      <p class="text-sm text-muted-foreground">
        {#if stale}
          <span data-testid="people-updating">Updating the list…</span>
        {:else if data}
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
        onValueChange={(value) => value && showLens(parseLens(value))}
        variant="segmented"
        size="sm"
        aria-label="View"
        disabled={!shown}
        class="w-full @2xl:w-fit"
      >
        <ToggleGroup.Item value="now" class="flex-1 @2xl:flex-none">Now</ToggleGroup.Item>
        <ToggleGroup.Item value="review" class="flex-1 @2xl:flex-none">Review</ToggleGroup.Item>
      </ToggleGroup.Root>
      <Button
        variant="outline"
        size="sm"
        href={resolve("/(authenticated)/people/rules")}
      >
        <Flag class="size-4" aria-hidden="true" />
        Your flag rules
      </Button>
      {#if lens === "now"}
        <Button
          variant="outline"
          size="sm"
          href={wallLocation}
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

  {#if fellBack}
    <p class="text-sm text-muted-foreground"><TabFallback fallback={fellBack} /></p>
  {/if}

  {#snippet controls()}
    {#if shown}
      <div class="space-y-3" data-testid="people-query">
        {#if openView?.groupGone}
          <p class="text-sm text-muted-foreground" data-testid="people-view-group-gone">
            This view has an "In a group" filter for a group you are no longer in. That filter matches no one.
          </p>
        {/if}
        {#if openView?.teamGone}
          <p class="text-sm text-muted-foreground" data-testid="people-view-team-gone">
            This view has a "Followed through a team" filter. You follow no one through that team, so that
            filter matches no one.
          </p>
        {/if}
        <div class="flex flex-wrap items-center gap-2">
          <PeopleSearch value={query.search} onSearch={search} />
          {#if canSaveView || worthKeeping}
            <div class="flex flex-wrap items-center gap-2 @2xl:ml-auto">
              {#if canSaveView}
                <span class="text-sm text-muted-foreground" data-testid="people-view-unsaved">
                  You changed this view. The changes are not saved.
                </span>
                <Button size="sm" disabled={saving || !canKeepViews} onclick={saveView}>Save view</Button>
              {/if}
              {#if worthKeeping}
                <Button
                  variant="outline"
                  size="sm"
                  disabled={atViewLimit || saving || !canKeepViews}
                  onclick={startNaming}
                >
                  Save as new view
                </Button>
              {/if}
            </div>
          {/if}
        </div>
        {#if (canSaveView || worthKeeping) && !canKeepViews}
          <p class="text-sm text-muted-foreground" data-testid="people-views-demo">
            You cannot save views in the demo.
          </p>
        {/if}
        {#if worthKeeping && atViewLimit}
          <p class="text-sm text-muted-foreground" data-testid="people-views-limit">
            You have {viewLimit} views, the maximum. Delete a view to save a new view.
          </p>
        {/if}
        {#if saveError}
          <p class="text-sm text-destructive" role="alert">{saveError}</p>
        {/if}
        <PeopleFilters filters={query.filters} groups={groupsQuery.current} onChange={filterBy} />
        {#if stillChecking > 0}
          <p class="text-sm text-muted-foreground" data-testid="people-still-checking">
            {plural(stillChecking, [
              "Still checking # person. The list may grow.",
              "Still checking # people. The list may grow.",
            ])}
          </p>
        {/if}
      </div>
    {/if}
  {/snippet}

  {#snippet list()}
    <div class="space-y-6" aria-busy={stale ? "true" : undefined} data-testid="people-list">
      {#if loadError}
        <div class="space-y-2">
          <p class="text-sm text-muted-foreground">
            The list of people did not load.
          </p>
          <Button
            variant="outline"
            size="sm"
            onclick={() => overviewQuery?.refresh()}
          >
            Try again
          </Button>
        </div>
      {:else if (!overviewQuery || overviewQuery.loading || rereadingGroups || rereadingViews) && !data}
        <p class="text-sm text-muted-foreground">Loading…</p>
      {:else if data}
        {#if group && hiddenCount > 0 && !stale}
          <p class="text-sm text-muted-foreground"><HiddenInGroup count={hiddenCount} /></p>
        {/if}
        {#if people.length === 0 && stale}
          <!-- A kept answer with no one in it says nothing of the query being read. -->
        {:else if people.length === 0 && !isEmptyQuery(query)}
          <EmptyState
            variant="dashed"
            art="people-group"
            title="No people match"
            headingLevel={2}
            body="Change the search or the filters to see more people."
          />
        {:else if people.length === 0 && group && hiddenCount > 0}
          <EmptyState
            variant="dashed"
            art="people-group"
            title="You cannot see anyone in this group"
            headingLevel={2}
            body="Each person's site decides who on the team can see them."
          />
        {:else if people.length === 0 && group}
          <EmptyState
            variant="dashed"
            art="people-group"
            title="You can see no people in this group"
            headingLevel={2}
            body="A manager of the team chooses the people in each group."
          />
        {:else if people.length === 0}
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
            <div class={["min-w-0 flex-1 transition-opacity", stale && "opacity-60"]} data-testid="people-rows">
              <PeopleRoster
                bind:this={roster}
                labelledby={headingId}
                people={rows}
                {review}
                sort={query.sort}
                onSort={sortBy}
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
  {/snippet}

  {#if groups.length > 0 || views.length > 0}
    <PeopleTabs {groups} {views} {tab} onChange={choose}>
      {#snippet actions()}
        {#if views.length > 0}
          <Button variant="ghost" size="sm" disabled={!canKeepViews} onclick={() => (managing = true)}>
            <Settings2 class="size-4" aria-hidden="true" />
            Manage views
          </Button>
        {/if}
      {/snippet}
      {@render controls()}
      {@render list()}
    </PeopleTabs>
  {:else}
    {@render controls()}
    {@render list()}
  {/if}
</div>

<PeopleViewsManager
  bind:open={managing}
  {views}
  limit={viewLimit}
  onDeleting={(viewId) => (deletingView = viewId)}
  onDeleted={deleted}
/>

<Dialog.Root bind:open={naming}>
  <Dialog.Content class="sm:max-w-md">
    <form class="space-y-4" onsubmit={saveAsNew} data-testid="people-view-new">
      <Dialog.Header>
        <Dialog.Title>Save as new view</Dialog.Title>
        <Dialog.Description>
          The view keeps the search, the filters, the sort and the lens. Only you see your views.
        </Dialog.Description>
      </Dialog.Header>
      {#if group}
        <p class="text-sm text-muted-foreground" data-testid="people-view-from-group">
          A view starts from everyone you follow, not only this group. To keep to this group, add the
          filter "In a group".
        </p>
      {/if}
      <div class="space-y-1">
        <Label for="people-view-name">Name</Label>
        <Input
          id="people-view-name"
          maxlength={VIEW_NAME_MAX_LENGTH}
          aria-invalid={nameTaken ? true : undefined}
          aria-describedby={nameTaken ? "people-view-name-error" : undefined}
          bind:value={newName}
        />
        {#if nameError}
          <p id="people-view-name-error" class="text-sm text-destructive" role="alert">{nameError}</p>
        {/if}
      </div>
      <Dialog.Footer>
        <Button type="button" variant="ghost" onclick={() => (naming = false)}>Cancel</Button>
        <Button type="submit" disabled={saving || newName.trim() === ""}>Save as new view</Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
