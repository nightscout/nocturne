<script lang="ts">
  import { afterNavigate, goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import RevealableOverlay from "#lib/components/shared/RevealableOverlay.svelte";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Now } from "#lib/hooks/now.svelte.js";
  import { plural } from "#lib/utils/plural.js";
  import { time } from "#lib/utils/formatting.js";
  import { createOverlayReveal } from "#lib/utils/overlay-reveal.svelte.js";
  import { Key } from "@nocturne/ui/keys";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import { followPeople } from "./people-overview.svelte.js";
  import { isFlagged } from "./person.js";
  import { setReadState } from "./read-state.js";
  import WallFlagged from "./WallFlagged.svelte";
  import WallLegend from "./WallLegend.svelte";
  import WallLine from "./WallLine.svelte";
  import {
    bandCapacity,
    bandColumns,
    intoColumns,
    wallColumnCapacity,
    wallStep,
  } from "./wall-layout.js";

  const overview = followPeople();
  const query = overview.query;
  const reveal = createOverlayReveal({ revealOnPointerMove: true });
  const clock = new Now(1000);

  setReadState({
    get disconnected() {
      return !!query.error;
    },
    get now() {
      return clock.current;
    },
  });

  // The query may drop its value when a refresh fails; the wall keeps the last read that worked.
  let lastRead = $state<{ people: TenantOverviewItem[]; at: Date } | null>(null);
  $effect.pre(() => {
    const current = query.current;
    if (current) lastRead = { people: current.tenants ?? [], at: new Date() };
  });

  const people = $derived(lastRead?.people ?? []);
  const pending = $derived(!lastRead && !query.error);

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
  const capacity = $derived(wallColumnCapacity(columnsWidth, columnsHeight));
  const columns = $derived(
    capacity ? intoColumns(rest, capacity.rows, capacity.columns) : [rest]
  );
  const hidden = $derived(capacity ? rest.slice(columns.flat().length) : []);
  const hiddenFlagged = $derived(hidden.filter(isFlagged).length);

  let fromPeople = false;
  afterNavigate(({ from }) => {
    fromPeople = from?.route.id === "/(authenticated)/people";
  });

  function leave() {
    if (fromPeople) history.back();
    else void goto(resolve("/(authenticated)/people"), { replaceState: true });
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
            {#each column as person (person.tenantId)}
              <WallLine {person} {step} />
            {/each}
          </ul>
        {/each}
      </div>
    </div>
  {:else if lastRead}
    <p class="m-auto text-2xl text-muted-foreground">You do not follow anyone.</p>
  {:else}
    <div class="min-h-0 flex-1" bind:clientWidth={columnsWidth}></div>
  {/if}

  <footer class="shrink-0 space-y-1 {step.footerClass}">
    {#if people.length > 0}
      <WallLegend {people} />
    {/if}
    <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
      <div class="flex flex-wrap items-center gap-3" data-testid="wall-connection">
        {#if query.error && lastRead}
          <Badge variant="overlay">
            Connection lost. Showing readings from {time(lastRead.at)}.
          </Badge>
        {:else if query.error}
          <span class="text-muted-foreground">The list of people did not load.</span>
          <Button variant="overlay" size="sm" onclick={() => query.refresh()}>Try again</Button>
        {:else if pending}
          <span class="text-muted-foreground">Loading the list of people.</span>
        {:else if overview.live}
          <span class="text-muted-foreground">Readings update as they arrive.</span>
        {:else}
          <span class="text-muted-foreground">Readings refresh every minute.</span>
        {/if}
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
