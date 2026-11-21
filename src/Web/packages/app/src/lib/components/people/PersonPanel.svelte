<script lang="ts">
  import { getPerson } from "#lib/api/generated/myTenants.generated.remote.js";
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Sheet from "#lib/components/ui/sheet/index.js";
  import { Key } from "@nocturne/ui/keys";
  import { bgLabel } from "#lib/utils/formatting.js";
  import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import X from "@lucide/svelte/icons/x";
  import AttentionReasons from "./AttentionReasons.svelte";
  import PersonDayTrace from "./PersonDayTrace.svelte";
  import PersonEntries from "./PersonEntries.svelte";
  import PersonReading from "./PersonReading.svelte";
  import PersonStatusLine from "./PersonStatusLine.svelte";
  import {
    PERSON_PANEL_HEADING_ID,
    PERSON_POLL_MS,
    personHref,
    personName,
  } from "./person.js";

  interface Props {
    /** The person's item from the overview: it names the person while the read is pending. */
    person: TenantOverviewItem;
    baseDomain: string | null;
    /** A sheet carries the dialog's own title and Escape handling. */
    placement: "side" | "sheet";
    onClose: () => void;
  }

  const { person, baseDomain, placement, onClose }: Props = $props();

  const name = $derived(personName(person));
  const siteHref = $derived(personHref(person, baseDomain));
  const query = $derived(
    person.tenantId ? getPerson({ tenantId: person.tenantId }) : null
  );
  // A response for another person than the open one is never shown.
  const detail = $derived.by(() => {
    const current = query?.current;
    return current?.person?.tenantId === person.tenantId ? current : undefined;
  });
  const failed = $derived(!query || !!query.error);

  pollWhileVisible(() => query?.refresh(), PERSON_POLL_MS);

  function onKeydown(event: KeyboardEvent) {
    if (placement === "side" && event.key === Key.Escape && !event.defaultPrevented) {
      onClose();
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

{#snippet heading()}
  <span class="min-w-0 flex-1 truncate text-base font-semibold" title={name}>
    {name}
  </span>
{/snippet}

<div class="flex min-h-0 flex-col divide-y divide-border" data-testid="person-panel">
  <header class="space-y-2 p-4">
    <div class="flex items-center gap-2">
      {#if placement === "sheet"}
        <Sheet.Title class="flex min-w-0 flex-1">{@render heading()}</Sheet.Title>
      {:else}
        <h2 id={PERSON_PANEL_HEADING_ID} class="flex min-w-0 flex-1">{@render heading()}</h2>
      {/if}
      <Button variant="ghost" size="icon-sm" aria-label="Close" onclick={onClose}>
        <X aria-hidden="true" />
      </Button>
    </div>
    {#if siteHref}
      <!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- an absolute cross-subdomain URL, not an app route -->
      <a href={siteHref}
        class="inline-flex items-center gap-1 rounded-sm text-sm text-primary outline-none hover:underline focus-visible:ring-3 focus-visible:ring-ring/50"
      >
        Open {name}'s site
        <ExternalLink class="size-3.5" aria-hidden="true" />
      </a>
    {/if}
  </header>

  {#if failed}
    <div class="space-y-2 p-4">
      <p class="text-sm text-muted-foreground">This person's data did not load.</p>
      <Button variant="outline" size="sm" onclick={() => query?.refresh()}>
        Try again
      </Button>
    </div>
  {:else if !detail}
    <p class="p-4 text-sm text-muted-foreground">Loading…</p>
  {:else if detail.person}
    {@const shown = detail.person}
    <section class="space-y-2 p-4" aria-label="Now">
      <PersonReading person={shown} />
      <AttentionReasons person={shown} />
    </section>
    <section class="space-y-2 p-4">
      <div class="flex items-baseline justify-between gap-2">
        <h3 class="text-sm font-semibold">Last 24 hours</h3>
        <span class="text-xs text-muted-foreground">{bgLabel()}</span>
      </div>
      <PersonDayTrace {detail} class="h-40 w-full" />
    </section>
    <section class="p-4 empty:hidden">
      <PersonStatusLine person={shown} />
    </section>
    <section class="space-y-2 p-4">
      <h3 class="text-sm font-semibold">Recent entries</h3>
      <PersonEntries entries={detail.entries} />
    </section>
  {/if}
</div>
