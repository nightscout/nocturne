<!--
  The signals the lens can draw, in collapsible families. A signal that cannot be placed now is
  disabled, with its reason written beside it (a reason shared by several is written once, above
  them). Signals only other lenses draw sit last, under one closed disclosure; each one switches to
  its lens and places the signal. A filter field appears once the lens offers more than 12 signals.
-->
<script lang="ts">
  import { signalLabel, lensLabel, familyLabel } from "./vocabulary-words.svelte.js";
  import { SvelteSet } from "svelte/reactivity";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import Check from "@lucide/svelte/icons/check";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Swatch from "./Swatch.svelte";
  import {
    FAMILY_LANES,
    signal as signalOf,
    type Family,
    type SignalId,
  } from "./vocabulary.js";
  import type { Workspace } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
  }

  let { ws }: Props = $props();

  const FILTER_AFTER = 12;

  let filter = $state("");

  const options = $derived(ws.signals);
  const elsewhere = $derived(ws.elsewhere);
  const query = $derived(filter.trim().toLowerCase());
  const visible = $derived(
    options.length > FILTER_AFTER && query
      ? options.filter((o) =>
          signalLabel(o.id).toLowerCase().includes(query)
        )
      : options
  );

  /** Reasons that disable two or more signals, written once above the list. */
  const shared = $derived.by(() => {
    const reasons = options.flatMap((o) => (o.reason ? [o.reason] : []));
    return reasons.filter(
      (r, i) => reasons.indexOf(r) === i && reasons.lastIndexOf(r) !== i
    );
  });

  function sharedId(reason: string): string {
    return `rail-shared-${shared.indexOf(reason)}`;
  }

  // svelte-ignore state_referenced_locally
  const placedFamilies = options
    .filter((o) => o.placed)
    .map((o) => signalOf(o.id).family);
  // svelte-ignore state_referenced_locally
  const openFamilies = new SvelteSet<Family>(
    placedFamilies.length > 0
      ? placedFamilies
      : options.slice(0, 1).map((o) => signalOf(o.id).family)
  );

  function swatch(id: SignalId) {
    return { kind: "solid" as const, color: `var(${signalOf(id).color})` };
  }
</script>

<nav aria-label="Signals" class="space-y-3" data-testid="signal-rail">
  {#if options.length > FILTER_AFTER}
    <Input
      size="sm"
      type="search"
      placeholder="Find a signal"
      aria-label="Find a signal"
      bind:value={filter}
    />
  {/if}

  {#each shared as reason (reason)}
    <p id={sharedId(reason)} class="px-2 text-xs text-muted-foreground">
      {reason}
    </p>
  {/each}

  <div class="space-y-1">
    {#each FAMILY_LANES as { family } (family)}
      {@const rows = visible.filter((o) => signalOf(o.id).family === family)}
      {#if rows.length > 0}
        <Collapsible.Root
          bind:open={
            () => query !== "" || openFamilies.has(family),
            (open: boolean) =>
              open ? openFamilies.add(family) : openFamilies.delete(family)
          }
        >
          <Collapsible.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button
                {...props}
                variant="subtle"
                size="xs"
                class="group w-full justify-start"
                data-family={family}
              >
                <ChevronRight
                  class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                  aria-hidden="true"
                />
                {familyLabel(family)}
                <span class="ml-auto tabular-nums">
                  {rows.filter((r) => r.placed).length || ""}
                </span>
              </Button>
            {/snippet}
          </Collapsible.Trigger>
          <Collapsible.Content>
            <ul class="space-y-0.5 pb-1">
              {#each rows as row (row.id)}
                {@const own = row.reason && !shared.includes(row.reason)}
                <li>
                  <Button
                    variant="ghost"
                    size="sm"
                    class="w-full justify-start"
                    aria-pressed={row.placed}
                    disabled={!row.placed && row.reason !== null}
                    aria-describedby={row.reason
                      ? own
                        ? `rail-reason-${row.id}`
                        : sharedId(row.reason)
                      : undefined}
                    onclick={() => ws.toggleSignal(row.id)}
                    data-signal={row.id}
                  >
                    <Swatch swatch={swatch(row.id)} />
                    <span class="truncate">{signalLabel(row.id)}</span>
                    {#if row.placed}
                      <Check class="ml-auto" aria-hidden="true" />
                    {/if}
                  </Button>
                  {#if own}
                    <p
                      id="rail-reason-{row.id}"
                      class="px-2 pb-1 pl-8 text-xs text-muted-foreground"
                    >
                      {row.reason}
                    </p>
                  {/if}
                </li>
              {/each}
            </ul>
          </Collapsible.Content>
        </Collapsible.Root>
      {/if}
    {/each}
    {#if query && visible.length === 0}
      <p class="px-2 text-xs text-muted-foreground">
        No signal in this view has that name.
      </p>
    {/if}
  </div>

  {#if ws.noAccess.length > 0}
    <div class="border-t border-border pt-2">
      <Collapsible.Root>
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button
              {...props}
              variant="subtle"
              size="xs"
              class="group w-full justify-start"
              data-testid="rail-no-access"
            >
              <ChevronRight
                class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                aria-hidden="true"
              />
              Not in your access ({ws.noAccess.length})
            </Button>
          {/snippet}
        </Collapsible.Trigger>
        <Collapsible.Content>
          <ul class="space-y-0.5 pt-1">
            {#each ws.noAccess as id (id)}
              <li>
                <Button variant="menu" size="menu" disabled aria-describedby="rail-no-access-why">
                  <Swatch swatch={swatch(id)} />
                  <span class="truncate">{signalLabel(id)}</span>
                </Button>
              </li>
            {/each}
          </ul>
          <p id="rail-no-access-why" class="px-2 pt-1 text-xs text-muted-foreground">
            Your access cannot read this data.
          </p>
        </Collapsible.Content>
      </Collapsible.Root>
    </div>
  {/if}

  {#if elsewhere.length > 0}
    <div class="border-t border-border pt-2">
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button
            {...props}
            variant="subtle"
            size="xs"
            class="group w-full justify-start"
            data-testid="rail-elsewhere"
          >
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            In other views ({elsewhere.length})
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content>
        <ul class="space-y-0.5 pt-1">
          {#each elsewhere as row (`${row.id}:${row.lens}`)}
            <li>
              <Button
                variant="menu"
                size="menu"
                disabled={row.reason !== null}
                aria-describedby={row.reason
                  ? `elsewhere-${row.id}-${row.lens}`
                  : undefined}
                onclick={() => ws.placeIn(row.id, row.lens)}
                data-elsewhere={`${row.id}:${row.lens}`}
              >
                <Swatch swatch={swatch(row.id)} />
                <span class="truncate">
                  {signalLabel(row.id)}:
                  <span class="text-muted-foreground">
                    {lensLabel(row.lens)}
                  </span>
                </span>
                {#if row.placed}
                  <Check class="ml-auto shrink-0" aria-hidden="true" />
                {/if}
              </Button>
              {#if row.reason}
                <p
                  id="elsewhere-{row.id}-{row.lens}"
                  class="px-2 pb-1 pl-8 text-xs text-muted-foreground"
                >
                  {row.reason}
                </p>
              {/if}
            </li>
          {/each}
        </ul>
      </Collapsible.Content>
    </Collapsible.Root>
    </div>
  {/if}
</nav>
