<script lang="ts">
  import { afterNavigate, goto, replaceState } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { untrack } from "svelte";
  import { isHttpError } from "@sveltejs/kit";
  import { getGroups } from "#lib/api/generated/myTenants.generated.remote.js";
  import { getPeopleViews } from "#lib/api/generated/peopleViews.generated.remote.js";
  import { OverviewRuleSet, type TenantOverviewResponse } from "@nocturne/api-client";
  import RevealableOverlay from "#lib/components/shared/RevealableOverlay.svelte";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { plural } from "#lib/utils/plural.js";
  import { formatList, time } from "#lib/utils/formatting.js";
  import { createOverlayReveal } from "#lib/utils/overlay-reveal.svelte.js";
  import { Key } from "@nocturne/ui/keys";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import GroupName from "./GroupName.svelte";
  import HiddenInGroup from "./HiddenInGroup.svelte";
  import TabFallback from "./TabFallback.svelte";
  import { followPeople } from "./people-overview.svelte.js";
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
    encodeFilters,
    encodeSort,
    isEmptyQuery,
    readUnsaved,
    shownOf,
    shownOfView,
    withTabQuery,
    type Unsaved,
  } from "./people-query.js";
  import { isFlagged } from "./person.js";
  import { setReadState } from "./read-state.js";
  import WallFlagged from "./WallFlagged.svelte";
  import WallGroupHeading from "./WallGroupHeading.svelte";
  import { wallSections } from "./wall-groups.js";
  import WallLegend from "./WallLegend.svelte";
  import WallLine from "./WallLine.svelte";
  import {
    bandCapacity,
    bandColumns,
    intoColumns,
    wallColumnCapacity,
    wallStep,
  } from "./wall-layout.js";

  const address = (page.shallow?.url ?? page.url).searchParams;
  let tab = $state<PeopleTab>(tabOf(address));
  /** The search, filters and sort the People page had not saved when it opened the wall. */
  let unsaved = $state<Unsaved>(readUnsaved(address));
  let fellBack = $state<Fallback | null>(null);
  const group = $derived(groupOf(tab));
  const groupsQuery = getGroups();
  const viewsQuery = $derived(tab.kind === "view" ? getPeopleViews() : null);
  const viewId = $derived(viewOf(tab));
  const openView = $derived(
    viewId ? viewsQuery?.current?.views.find((summary) => summary.view.id === viewId)?.view : undefined
  );
  /** The tab's query with the unsaved changes over it; null while a view is not read. */
  const peopleQuery = $derived.by(() => {
    const own = tab.kind === "view" ? (openView ? shownOfView(openView) : null) : PLAIN;
    if (!own) return null;
    const { search, filters, sort } = unsaved;
    return shownOf(own, { search, filters, sort }).query;
  });
  // The wall is seen by everyone in the room, so it shows the rules its teams share and never one viewer's own.
  const overview = followPeople(
    () => (peopleQuery ? { tab, query: peopleQuery } : null),
    () => {
      void groupsQuery.refresh().catch(() => {});
      void viewsQuery?.refresh().catch(() => {});
    },
    OverviewRuleSet.Team
  );
  const query = $derived(overview.query);
  const groupNotFound = $derived(isHttpError(query?.error) && query.error.status === 404);
  const loadError = $derived(groupNotFound ? undefined : query?.error);
  const openGroup = $derived(
    group ? groupsQuery.current?.find((listed) => listed.id === group) : undefined
  );
  const filtered = $derived(!isEmptyQuery(peopleQuery ?? EMPTY_QUERY));
  const reveal = createOverlayReveal({ revealOnPointerMove: true });
  const clock = new Now(1000);

  // A view is judged by the views list alone, never by the groups.
  $effect(() => {
    if (tab.kind === "everyone") return;
    const kind = tab.kind;
    const reason =
      kind === "group"
        ? fallbackOf(tab.id, groupsQuery.current, !!groupsQuery.error, groupNotFound)
        : fallbackOf(
            tab.id,
            viewsQuery?.current?.views.map((summary) => summary.view),
            !!viewsQuery?.error,
            false
          );
    if (!reason) return;
    untrack(() => {
      tab = EVERYONE;
      unsaved = {};
      fellBack = { kind, reason };
      // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its query changed, already resolved
      replaceState(withTabQuery(new URL(page.url.href), EVERYONE, {}), {});
    });
  });

  setReadState({
    get disconnected() {
      return !!loadError;
    },
    get now() {
      return clock.current;
    },
  });

  // The query may drop its value when a refresh fails; the wall keeps the last read of its tab that worked.
  const readKey = $derived(
    [
      tabValue(tab),
      peopleQuery?.search ?? "",
      encodeFilters(peopleQuery?.filters ?? []),
      encodeSort(peopleQuery?.sort ?? null),
    ].join(" ")
  );
  let lastRead = $state<{ key: string; response: TenantOverviewResponse; at: Date } | null>(null);
  $effect.pre(() => {
    const current = query?.current;
    if (current) lastRead = { key: readKey, response: current, at: new Date() };
  });
  const kept = $derived(lastRead?.key === readKey ? lastRead : null);

  const people = $derived(kept?.response.tenants ?? []);
  const hiddenCount = $derived(kept?.response.hiddenCount ?? 0);
  const ruleTeams = $derived(kept?.response.ruleTeams ?? []);
  const pending = $derived(!kept && !loadError);

  let rootHeight = $state<number>();
  let columnsWidth = $state<number>();
  let columnsHeight = $state<number>();

  const step = $derived(wallStep(columnsWidth));
  const flagged = $derived(people.filter(isFlagged));
  const candidates = $derived(
    flagged.slice(0, bandCapacity(flagged.length, columnsWidth, rootHeight))
  );
  const bandKey = $derived(
    [
      columnsWidth,
      rootHeight,
      ...candidates.map((person) => `${person.tenantId}:${person.attention?.length ?? 0}`),
    ].join("|")
  );

  // The capacity is an estimate of a card's height. A card that ends below the band's clipped box
  // would be half drawn, so the cards that do not fit go to the columns. The cards are measured
  // when the list is first drawn for a set of candidates and screen size, and again whenever the
  // list changes size, as when a font arrives or a reason gets longer. A measurement counts only
  // the cards drawn, so it can only keep the count or lower it: cutting the band changes nothing a
  // later measurement can use to cut again, and a card that shrinks back does not restore one.
  let bandBox = $state<HTMLElement>();
  let bandFit = $state<{ key: string; count: number }>();
  const band = $derived(
    bandFit?.key === bandKey ? candidates.slice(0, bandFit.count) : candidates
  );
  $effect(() => {
    const key = bandKey;
    const box = bandBox;
    const list = box?.querySelector(":scope > ul");
    if (!box || !list) return;
    const observer = new ResizeObserver(() => {
      const floor = box.getBoundingClientRect().bottom + 0.5;
      const count = [...list.children].filter(
        (card) => card.getBoundingClientRect().bottom <= floor
      ).length;
      if (bandFit?.key === key && bandFit.count === count) return;
      bandFit = { key, count };
    });
    observer.observe(list);
    return () => observer.disconnect();
  });
  const rest = $derived(people.filter((person) => !band.includes(person)));
  // Group headings regroup the API's order, so they stand only over everyone in that order.
  const sections = $derived(
    tab.kind === "everyone" && !peopleQuery?.sort
      ? wallSections(rest, groupsQuery.current)
      : [{ heading: null, people: rest }]
  );
  const capacity = $derived(wallColumnCapacity(columnsWidth, columnsHeight));
  const columns = $derived(
    capacity
      ? intoColumns(sections, capacity.rows, capacity.columns)
      : intoColumns(sections, Number.POSITIVE_INFINITY, 1)
  );
  const shown = $derived(
    new Set(columns.flat().flatMap((slot) => (slot.kind === "person" ? [slot.person] : [])))
  );
  const hidden = $derived(rest.filter((person) => !shown.has(person)));
  const hiddenFlagged = $derived(hidden.filter(isFlagged).length);

  let fromPeople = false;
  afterNavigate(({ from }) => {
    fromPeople = from?.route.id === "/(authenticated)/people";
  });

  function leave() {
    if (fromPeople) {
      history.back();
      return;
    }
    const url = withTabQuery(new URL(resolve("/(authenticated)/people"), page.url.href), tab, unsaved);
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the People page's resolved path with the tab's query
    void goto(url.pathname + url.search, { replaceState: true });
  }

  function onkeydown(event: KeyboardEvent) {
    reveal.keydown();
    if (event.key === Key.Escape) leave();
  }

</script>

<svelte:head>
  <title>People wall display - Nocturne</title>
</svelte:head>

<svelte:window
  {onkeydown}
  onpointerdown={reveal.pointerdown}
  onpointermove={reveal.pointermove}
/>

<RevealableOverlay {reveal} data-testid="wall-nav">
  <Button variant="overlay" size="sm" onclick={leave}>
    <ArrowLeft class="size-4" />Back
  </Button>
</RevealableOverlay>

<main
  class="dark @container fixed inset-0 flex flex-col gap-3 overflow-hidden bg-background p-4 text-foreground"
  aria-label="People wall display"
  data-testid="people-wall"
  bind:clientHeight={rootHeight}
>
  {#if people.length > 0}
    {#if band.length > 0}
      <div
        class="max-h-2/5 shrink-0 overflow-hidden"
        data-testid="wall-band-box"
        bind:this={bandBox}
      >
        <ul class="grid gap-2 {bandColumns(columnsWidth).gridClass}" data-testid="wall-band">
          {#each band as person (person.tenantId)}
            <WallFlagged {person} {step} />
          {/each}
        </ul>
      </div>
    {/if}
    <div
      class="min-h-0 flex-1 overflow-hidden"
      bind:clientWidth={columnsWidth}
      bind:clientHeight={columnsHeight}
    >
      <div class="flex h-full gap-x-4" data-testid="wall-columns">
        {#each columns as column, index (index)}
          <ul class="flex min-w-0 flex-1 flex-col">
            {#each column as slot (slot.kind === "person" ? slot.person.tenantId : `heading:${slot.heading.group?.id ?? ""}`)}
              {#if slot.kind === "heading"}
                <WallGroupHeading heading={slot.heading} {step} repeated={slot.repeated} />
              {:else}
                <WallLine person={slot.person} {step} />
              {/if}
            {/each}
          </ul>
        {/each}
      </div>
    </div>
  {:else if kept}
    <p class="m-auto text-2xl text-muted-foreground">
      {#if filtered}
        No people match.
      {:else if group && hiddenCount > 0}
        You cannot see anyone in this group.
      {:else if group}
        You can see no people in this group.
      {:else}
        You do not follow anyone.
      {/if}
    </p>
  {:else}
    <div class="min-h-0 flex-1" bind:clientWidth={columnsWidth}></div>
  {/if}

  <footer class="shrink-0 space-y-1 {step.footerClass}">
    {#if people.length > 0}
      <WallLegend {people} />
    {/if}
    <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
      <div class="flex flex-wrap items-center gap-x-4 gap-y-1">
        {#if openGroup}
          <span class="font-semibold" data-testid="wall-group"><GroupName group={openGroup} /></span>
        {/if}
        {#if openView}
          <span class="font-semibold" data-testid="wall-view">{openView.name}</span>
        {/if}
        {#if group && hiddenCount > 0}
          <span class="font-medium" data-testid="wall-hidden-in-group">
            <HiddenInGroup count={hiddenCount} />
          </span>
        {/if}
        {#if fellBack}
          <span class="text-muted-foreground" data-testid="wall-fallback">
            <TabFallback fallback={fellBack} />
          </span>
        {/if}
        <span class="text-muted-foreground" data-testid="wall-rules">
          {#if ruleTeams.length === 0}
            Flagged by Nocturne's fixed rules.
          {:else if ruleTeams.length === 1}
            Flagged by Nocturne's fixed rules and {ruleTeams[0].name}'s rules.
          {:else}
            Flagged by Nocturne's fixed rules and the rules of {ruleTeams.length} teams: {formatList(
              ruleTeams.map((team) => team.name ?? "")
            )}.
          {/if}
        </span>
        <div class="flex flex-wrap items-center gap-3" data-testid="wall-connection">
          {#if loadError && kept}
            <Badge variant="overlay">
              Connection lost. Showing readings from {time(kept.at)}.
            </Badge>
          {:else if loadError}
            <span class="text-muted-foreground">The list of people did not load.</span>
            <Button variant="overlay" size="sm" onclick={() => query?.refresh()}>Try again</Button>
          {:else if pending}
            <span class="text-muted-foreground">Loading the list of people.</span>
          {:else if overview.live}
            <span class="text-muted-foreground">Readings update as they arrive.</span>
          {:else}
            <span class="text-muted-foreground">Readings refresh every minute.</span>
          {/if}
        </div>
      </div>
      <div class="flex flex-wrap items-center gap-x-4">
        {#if hidden.length > 0}
          <span class="font-medium" data-testid="wall-not-shown">
            {plural(hidden.length, ["# more person is not shown.", "# more people are not shown."])}
            {#if hiddenFlagged > 0}
              {#if hidden.length === 1}
                This person is flagged.
              {:else}
                {plural(hiddenFlagged, ["# of them is flagged.", "# of them are flagged."])}
              {/if}
            {/if}
          </span>
        {/if}
        <time
          class="font-medium tabular-nums {step.clockClass}"
          datetime={new Date(clock.current).toISOString()}
          data-testid="wall-clock"
        >
          {time(new Date(clock.current), { seconds: true })}
        </time>
      </div>
    </div>
  </footer>
</main>
