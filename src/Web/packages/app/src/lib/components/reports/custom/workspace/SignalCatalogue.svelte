<!--
  Every signal of the catalogue, whatever the lens: a dialog on a wide screen, a full-height sheet on a
  phone. The search field finds a signal in every lens by its name, description or other words. With no
  search, the list starts with the signals on the chart, the starred and the recently used ones, then the
  sections and their groups. A signal with no data in the range, or that the reader cannot read, is in a
  closed group at the end. A signal the lens cannot draw offers to switch the view, or to put it in a new
  chart. Signals whose data does not cover the chart's range are counted once, above the list, with the
  range that fits. A chart held as a spec gave it lists only the signals along its axis
  (catalogue-source.ts).
-->
<script lang="ts">
  import { formatList } from "#lib/utils/formatting.js";
  import { tick } from "svelte";
  import { getCustomReportSignalAvailability } from "#lib/api/generated/customReports.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import * as InputGroup from "#lib/components/ui/input-group/index.js";
  import * as Item from "#lib/components/ui/item/index.js";
  import * as Sheet from "#lib/components/ui/sheet/index.js";
  import { IsMobile } from "#lib/hooks/is-mobile.svelte.js";
  import Check from "@lucide/svelte/icons/check";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Search from "@lucide/svelte/icons/search";
  import Star from "@lucide/svelte/icons/star";
  import type { Snippet } from "svelte";
  import type { SignalId } from "./catalogue-vocabulary.js";
  import type { CatalogueSource } from "./catalogue-source.js";
  import type { Report } from "./report.svelte.js";
  import { searchSignals } from "./signal-search.js";
  import type { SignalPicks } from "./signal-picks.svelte.js";
  import Swatch from "./Swatch.svelte";
  import {
    daysText,
    lensLabel,
    lensPhrase,
    rangeLabel,
    searchWords,
    sectionLabel,
    signalDescription,
    signalGroupLabel,
    signalLabel,
    signalPhrase,
  } from "./vocabulary-words.svelte.js";
  import type { CatalogueEntry } from "./workspace.svelte.js";

  interface Props {
    report: Report;
    /** The chart the catalogue was opened from. */
    source: CatalogueSource;
    picks: SignalPicks;
    open: boolean;
    /** The control that opens the catalogue. */
    trigger: Snippet<[Record<string, unknown>]>;
  }

  let { report, source, picks, open = $bindable(), trigger }: Props = $props();

  const vocab = $derived(source.vocab);
  const mobile = new IsMobile();
  /** The sheet has no padding of its own; the dialog has. */
  const pad = $derived(mobile.current);

  let query = $state("");
  let search = $state<HTMLInputElement | null>(null);
  /** The row whose other views are open, by its list and signal. */
  let expanded = $state<string | null>(null);
  /** What the last action did, said once to a screen reader. */
  let said = $state("");
  /** Why the last action could not be done. */
  let refused = $state<string | null>(null);

  const entries = $derived(open ? source.entries : []);
  const byId = $derived(new Map(entries.map((e) => [e.id, e])));
  const ids = $derived(entries.map((e) => e.id));

  const availability = $derived(open ? getCustomReportSignalAvailability({ hours: source.rangeHours }) : null);
  const presence = $derived(
    new Map((availability?.current?.signals ?? []).map((s) => [s.signal, s.state]))
  );

  const noAccess = $derived(entries.filter((e) => e.noAccess).map((e) => e.id));
  const noData = $derived(entries.filter((e) => !e.noAccess && presence.get(e.id) === "none").map((e) => e.id));
  const listed = $derived(new Set(entries.filter((e) => !e.noAccess && presence.get(e.id) !== "none").map((e) => e.id)));

  const onChart = $derived(source.onChart);
  const starred = $derived(picks.starred.filter((id) => listed.has(id)));
  const recent = $derived(picks.recent.filter((id) => listed.has(id)));

  const sections = $derived(
    vocab.sections
      .map((section) => ({
        id: section.id,
        groups: section.groups
          .map((group) => ({
            id: group,
            signals: ids.filter((id) => {
              const s = vocab.signal(id);
              return listed.has(id) && s?.section === section.id && s.group === group;
            }),
          }))
          .filter((g) => g.signals.length > 0),
      }))
      .filter((s) => s.groups.length > 0)
  );

  function unitOf(id: SignalId): string | null {
    const unit = vocab.signal(id)?.unit ?? null;
    return unit === "glucose" ? source.glucoseUnit : unit;
  }

  function wordsOf(id: SignalId) {
    const s = vocab.signal(id);
    return {
      name: signalLabel(id),
      other: [
        signalDescription(id),
        s ? sectionLabel(s.section) : "",
        s ? signalGroupLabel(s.group) : "",
        unitOf(id) ?? "",
        ...searchWords(s?.searchKeys ?? []),
      ],
    };
  }

  const results = $derived(query.trim() ? searchSignals(query, ids, wordsOf) : null);
  const found = $derived(results?.filter((id) => listed.has(id)) ?? []);

  /** Reasons that keep two or more signals off the chart, written once above the list. */
  const shared = $derived.by(() => {
    const shown = results === null ? entries : entries.filter((e) => results.includes(e.id));
    const reasons = shown.flatMap((e) => (e.drawable && !e.placed && e.reason ? [e.reason] : []));
    return reasons.filter((r, i) => reasons.indexOf(r) === i && reasons.lastIndexOf(r) !== i);
  });

  const scopeText = $derived(source.everyView ? "Search every signal in every view." : "Search the signals this chart can show.");

  const needs = $derived(open ? source.rangeNeeds : []);


  function needId(hours: number): string {
    return `catalogue-range-${hours}`;
  }

  function describedBy(e: CatalogueEntry, key: string): string {
    const ids = [`${key}-unit`, `${key}-about`];
    if (e.drawable && !e.placed) {
      if (e.needsShorter) ids.push(needId(vocab.cellOf(source.lens, e.id)?.maxRangeHours ?? 0));
      else if (e.reason) ids.push(shared.includes(e.reason) ? `catalogue-shared-${shared.indexOf(e.reason)}` : `${key}-why`);
    }
    return ids.join(" ");
  }

  function toggle(e: CatalogueEntry, key: string) {
    if (e.noAccess) return;
    if (!e.drawable) {
      expanded = expanded === key ? null : key;
      return;
    }
    const placing = !e.placed;
    refused = source.toggle(e.id);
    if (refused) return;
    if (placing) picks.used(e.id);
    said = placing ? `${signalLabel(e.id)} is on the chart.` : `${signalLabel(e.id)} is off the chart.`;
  }

  function switchTo(id: SignalId, lens: string) {
    refused = source.placeIn(id, lens);
    if (refused) return;
    picks.used(id);
    expanded = null;
    open = false;
  }

  async function newChart(id: SignalId, lens: string) {
    refused = report.addReason("chart");
    if (refused) return;
    picks.used(id);
    expanded = null;
    open = false;
    // The open block changes, so this block's view goes; the dialog closes first.
    await tick();
    if (report.addChartWith(id, lens)) return;
    await tick();
    document
      .querySelector<HTMLElement>("[data-block-kind='chart'][data-open='true'] [data-testid='add-signal']")
      ?.focus();
  }

  function useRange(hours: number) {
    refused = source.useRange(hours);
  }

  $effect(() => {
    if (!open) {
      query = "";
      expanded = null;
      said = "";
      refused = null;
    }
  });
</script>

{#snippet row(id: SignalId, list: string)}
  {@const e = byId.get(id)}
  {#if e}
    {@const key = `${list}-${id}`}
    {@const unit = unitOf(id)}
    {@const lenses = vocab.lensesOf(id)}
    {@const blocked = e.noAccess || (e.drawable && !e.placed && (e.needsShorter || e.reason !== null))}
    {@const isStarred = picks.isStarred(id)}
    <li class="border-b border-border last:border-b-0" data-catalogue-row={id}>
      <div class="flex items-start gap-1">
        <Item.Root
          variant="ghost"
          size="sm"
          class="min-h-11 flex-1 items-start"
          aria-pressed={e.drawable ? e.placed : undefined}
          aria-expanded={!e.drawable && !e.noAccess ? expanded === key : undefined}
          aria-controls={!e.drawable && !e.noAccess ? `${key}-views` : undefined}
          aria-labelledby="{key}-name"
          aria-describedby={describedBy(e, key)}
          disabled={blocked}
          onclick={() => toggle(e, key)}
          data-signal={id}
        >
          <span class="pt-1.5"><Swatch swatch={{ kind: "solid", color: vocab.signalColor(id) }} /></span>
          <span class="min-w-0 flex-1 space-y-0.5">
            <span class="flex flex-wrap items-baseline gap-x-2">
              <span id="{key}-name" class="text-sm font-medium">{signalLabel(id)}</span>
              <span id="{key}-unit" class="text-xs text-muted-foreground">{unit ?? ""}</span>
            </span>
            <span id="{key}-about" class="block text-xs text-muted-foreground">
              <span class="block">{signalDescription(id)}</span>
              <span class="mt-1 flex flex-wrap gap-x-3 gap-y-0.5">
                <span>Views: {formatList(lenses.map((l) => lensLabel(l)), "unit")}</span>
                {#if e.upTo !== null}<span>Up to {daysText(e.upTo)}</span>{/if}
                {#if presence.get(id) === "none"}<span>No data in this range</span>{/if}
                {#if e.accessReason}<span>{e.accessReason}</span>{/if}
              </span>
            </span>
          </span>
          {#if e.drawable && e.placed}
            <Check class="mt-1 size-4 shrink-0" aria-hidden="true" />
          {:else if !e.drawable && !e.noAccess}
            <ChevronRight
              class={["mt-1 size-4 shrink-0 text-muted-foreground transition-transform motion-reduce:transition-none", expanded === key && "rotate-90"]}
              aria-hidden="true"
            />
          {/if}
        </Item.Root>
        <Button
          variant="ghost"
          size="icon-touch"
          class="shrink-0"
          aria-pressed={isStarred}
          aria-label={`Star ${signalPhrase(id)}`}
          onclick={() => picks.toggleStar(id)}
          data-star={id}
        >
          <Star class={isStarred ? "fill-current text-foreground" : "text-muted-foreground"} aria-hidden="true" />
        </Button>
      </div>
      {#if e.drawable && !e.placed && !e.needsShorter && e.reason && !shared.includes(e.reason)}
        <p id="{key}-why" class="px-2 pb-2 pl-9 text-xs text-muted-foreground">{e.reason}</p>
      {/if}
      {#if !e.drawable && !e.noAccess && expanded === key}
        <div id="{key}-views" class="space-y-2 px-2 pb-3 pl-9">
          <p class="text-xs text-muted-foreground">{lensLabel(source.lens)} cannot show this signal.</p>
          <div class="flex flex-wrap gap-2">
            {#each e.otherLenses as lens (lens)}
              <Button variant="outline" size="touch-sm" onclick={() => switchTo(id, lens)} data-switch={lens}>
                Switch to {lensPhrase(lens)}
              </Button>
            {/each}
            {#if e.otherLenses.length > 0}
              <Button variant="outline" size="touch-sm" onclick={() => newChart(id, e.otherLenses[0])} data-testid="add-as-chart">
                Add as a new chart
              </Button>
            {/if}
          </div>
          {#if e.otherLenses.length > 0 && source.placeInRange(id, e.otherLenses[0]) !== null}
            <p class="text-xs text-muted-foreground">
              The view shows at most {daysText(source.placeInRange(id, e.otherLenses[0]) ?? 0)} with this signal.
            </p>
          {/if}
          {#if e.reason}<p class="text-xs text-muted-foreground">{e.reason}</p>{/if}
        </div>
      {/if}
    </li>
  {/if}
{/snippet}

{#snippet list(heading: string, signals: SignalId[], name: string)}
  {#if signals.length > 0}
    <section aria-labelledby="catalogue-{name}" class="space-y-1">
      <h3 id="catalogue-{name}" class="px-2 text-xs font-medium text-muted-foreground">{heading}</h3>
      <ul>
        {#each signals as id (id)}{@render row(id, name)}{/each}
      </ul>
    </section>
  {/if}
{/snippet}

{#snippet closedGroup(heading: string, signals: SignalId[], name: string)}
  {#if signals.length > 0}
    <div class="border-t border-border pt-2">
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="touch-sm" class="group w-full justify-start" data-testid="catalogue-{name}">
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            {heading} ({signals.length})
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content>
        <ul>
          {#each signals as id (id)}{@render row(id, name)}{/each}
        </ul>
      </Collapsible.Content>
    </Collapsible.Root>
    </div>
  {/if}
{/snippet}

{#snippet body()}
  <div class={["shrink-0 space-y-1 pr-8", pad && "px-4 pt-4"]}>
    {#if mobile.current}
      <Sheet.Title>Add a signal</Sheet.Title>
      <Sheet.Description>{scopeText}</Sheet.Description>
    {:else}
      <Dialog.Title>Add a signal</Dialog.Title>
      <Dialog.Description>{scopeText}</Dialog.Description>
    {/if}
  </div>
  <div class={["shrink-0 space-y-3 @container", pad && "px-4"]}>
    <InputGroup.Root>
      <InputGroup.Addon>
        <Search aria-hidden="true" />
      </InputGroup.Addon>
      <InputGroup.Input
        bind:ref={search}
        type="search"
        placeholder="Name, abbreviation or kind of data"
        aria-label="Search signals"
        aria-describedby="catalogue-found"
        bind:value={query}
        data-testid="catalogue-search"
      />
    </InputGroup.Root>
    <p id="catalogue-found" class="sr-only" aria-live="polite">
      {results === null ? "" : found.length === 1 ? "1 signal found." : `${found.length} signals found.`}
    </p>
    {#if needs.length > 0}
    <div class="divide-y divide-border rounded-md border border-border bg-muted/50 text-sm">
    {#each needs as need (need.hours)}
      <div
        id={needId(need.hours)}
        class="flex flex-wrap items-center justify-between gap-2 px-3 py-2"
        data-testid="range-need"
      >
        <span>
          {need.signals.length === 1 ? "1 signal needs" : `${need.signals.length} signals need`}
          {daysText(need.hours)} or less.
        </span>
        {#if need.preset}
          <Button variant="outline" size="touch-sm" onclick={() => useRange(need.preset?.hours ?? 0)} data-testid="use-range">
            Use {rangeLabel(need.preset.id)}
          </Button>
        {/if}
      </div>
    {/each}
    </div>
    {/if}
    {#each shared as reason, i (reason)}
      <p id="catalogue-shared-{i}" class="text-xs text-muted-foreground">{reason}</p>
    {/each}
  </div>
  <div class={["min-h-0 flex-1 space-y-4 overflow-y-auto overscroll-contain @container", pad ? "px-2 pb-4" : "-mx-2 px-2"]} data-testid="signal-catalogue">
    {#if results !== null}
      {#if found.length === 0}
        <p class="px-2 py-4 text-sm text-muted-foreground">No signal has that name. Try a different word.</p>
      {:else}
        {@render list("Results", found, "results")}
      {/if}
      {@render closedGroup("No data in this range", results.filter((id) => noData.includes(id)), "no-data")}
      {@render closedGroup("Not in your access", results.filter((id) => noAccess.includes(id)), "no-access")}
    {:else}
      {@render list("On this chart", onChart, "on-chart")}
      {@render list("Starred", starred, "starred")}
      {@render list("Recently used", recent, "recent")}
      {#each sections as section (section.id)}
        <div class="border-t border-border pt-2">
        <Collapsible.Root>
          <Collapsible.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="ghost" size="touch-sm" class="group w-full justify-start" data-section={section.id}>
                <ChevronRight
                  class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                  aria-hidden="true"
                />
                {sectionLabel(section.id)}
                <span class="ml-auto text-xs tabular-nums text-muted-foreground">
                  {section.groups.reduce((n, g) => n + g.signals.length, 0)}
                </span>
              </Button>
            {/snippet}
          </Collapsible.Trigger>
          <Collapsible.Content class="space-y-3 pt-1">
            {#each section.groups as group (group.id)}
              {@render list(signalGroupLabel(group.id), group.signals, `${section.id}-${group.id}`)}
            {/each}
          </Collapsible.Content>
        </Collapsible.Root>
        </div>
      {/each}
      {@render closedGroup("No data in this range", noData, "no-data")}
      {@render closedGroup("Not in your access", noAccess, "no-access")}
    {/if}
  </div>
  <p class="sr-only" aria-live="polite">{said}</p>
  {#if refused}
    <p class={["border-t border-border pt-2 text-xs text-muted-foreground", pad && "px-4 pb-2"]} role="status">{refused}</p>
  {/if}
{/snippet}

<Dialog.Root bind:open>
  <Dialog.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      {@render trigger(props)}
    {/snippet}
  </Dialog.Trigger>
  {#if mobile.current}
    <Sheet.Content
      side="bottom"
      class="h-dvh max-h-dvh gap-3"
      onOpenAutoFocus={(e: Event) => {
        e.preventDefault();
        search?.focus();
      }}
    >
      {@render body()}
      <div class="border-t border-border px-4 pb-4 pt-3 @container">
        <Sheet.Close>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="outline" size="touch" class="w-full">Done</Button>
          {/snippet}
        </Sheet.Close>
      </div>
    </Sheet.Content>
  {:else}
    <Dialog.Content
      class="flex h-[min(85dvh,46rem)] flex-col sm:max-w-2xl"
      onOpenAutoFocus={(e: Event) => {
        e.preventDefault();
        search?.focus();
      }}
    >
      {@render body()}
    </Dialog.Content>
  {/if}
</Dialog.Root>
