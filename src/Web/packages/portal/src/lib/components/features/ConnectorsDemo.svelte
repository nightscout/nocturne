<script lang="ts">
  import { Search, Check, Clock } from "@lucide/svelte";

  import { CONNECTORS, type Connector } from "$lib/data/connectors";

  const TYPE_TARGETS = ["Dexcom", "Libre", "Loop", "Tandem", "xDrip"];

  interface Props {
    height?: number;
  }
  let { height = 340 }: Props = $props();

  let query = $state("");
  let userTyping = $state(false);

  $effect(() => {
    if (userTyping) return;
    let cancelled = false;
    let ti = 0;
    let i = 0;
    let timer: ReturnType<typeof setTimeout>;

    const tick = () => {
      if (cancelled) return;
      const word = TYPE_TARGETS[ti];
      if (i <= word.length) {
        query = word.slice(0, i);
        i++;
        timer = setTimeout(tick, 130);
      } else {
        timer = setTimeout(() => {
          if (cancelled) return;
          let j = word.length;
          const erase = () => {
            if (cancelled) return;
            if (j >= 0) {
              query = word.slice(0, j);
              j--;
              timer = setTimeout(erase, 60);
            } else {
              i = 0;
              ti = (ti + 1) % TYPE_TARGETS.length;
              timer = setTimeout(tick, 400);
            }
          };
          erase();
        }, 1600);
      }
    };
    tick();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  });

  function matches(c: Connector, q: string): boolean {
    const lower = q.toLowerCase();
    return (
      c.name.toLowerCase().includes(lower) ||
      c.kind.toLowerCase().includes(lower) ||
      (c.aliases?.some((a) => a.toLowerCase().includes(lower)) ?? false)
    );
  }

  let filtered = $derived(
    query ? CONNECTORS.filter((c) => matches(c, query)) : CONNECTORS,
  );

  let activeMatches = $derived(filtered.filter((c) => !c.comingSoon).length);
  let comingSoonMatches = $derived(filtered.filter((c) => c.comingSoon).length);
</script>

<div
  class="rounded-xl overflow-hidden border border-white/10 bg-sunken flex flex-col h-(--demo-h)"
  style:--demo-h="{height}px"
>
  <!-- Search bar -->
  <div
    class="px-4.5 py-3.5 border-b border-white/8 flex items-center gap-3 bg-card/20 shrink-0"
  >
    <Search class="size-5 text-muted-foreground shrink-0" />
    <!-- eslint-disable-next-line no-restricted-syntax -- the demo's search bar is a borderless headline-size field in its own header row, which no Input size or variant draws -->
    <input
      class="flex-1 bg-transparent border-none outline-none text-lg text-foreground font-medium placeholder:text-muted-foreground/60 min-h-7 w-0"
      placeholder="Find your device or app"
      aria-label="Search connectors"
      bind:value={query}
      onfocus={() => (userTyping = true)}
      onblur={() => {
        if (!query) userTyping = false;
      }}
      autocomplete="off"
      spellcheck="false"
    />
    {#if !userTyping}
      <span
        class="cursor-blink ml-0.5 inline-block w-0.5 h-5 bg-brand align-middle pointer-events-none"
        aria-hidden="true"
      ></span>
    {/if}
    <span class="text-xs tabular-nums shrink-0 flex items-center gap-1.5">
      {#if query}
        <span class="text-brand px-2.5 py-1 rounded-full bg-brand/15 border border-brand/30">
          {activeMatches} match{activeMatches === 1 ? "" : "es"}
        </span>
        {#if comingSoonMatches > 0}
          <span class="text-muted-foreground px-2.5 py-1 rounded-full bg-muted/30 border border-border">
            {comingSoonMatches} coming soon
          </span>
        {/if}
      {:else}
        <span class="text-brand px-2.5 py-1 rounded-full bg-brand/15 border border-brand/30">
          {activeMatches} live
        </span>
        <span class="text-muted-foreground px-2.5 py-1 rounded-full bg-muted/30 border border-border">
          {comingSoonMatches} coming soon
        </span>
      {/if}
    </span>
  </div>

  <!-- Connector grid -->
  <div
    class="flex-1 p-3.5 overflow-y-auto grid grid-cols-[repeat(auto-fill,minmax(130px,1fr))] content-start gap-2"
  >
    {#each filtered as c (c.file)}
      {@const isMatch = !!query && matches(c, query)}
      <svelte:element
        this={c.comingSoon && c.issue ? 'a' : 'div'}
        href={c.comingSoon && c.issue ? `https://github.com/nightscout/nocturne/issues/${c.issue}` : undefined}
        target={c.comingSoon && c.issue ? "_blank" : undefined}
        rel={c.comingSoon && c.issue ? "noopener noreferrer" : undefined}
        class="flex items-center gap-2.5 px-3 py-2.5 rounded-lg border transition-all duration-200
                    {c.comingSoon
          ? 'opacity-50 bg-white/2 border-white/4 cursor-pointer hover:opacity-70'
          : isMatch
            ? 'bg-brand/14 border-brand/50'
            : 'bg-white/4 border-white/6'}"
      >
        <img
          src="/logos/{c.file}"
          alt={c.name}
          class="size-5 rounded object-cover shrink-0 {c.comingSoon ? 'grayscale' : ''}"
          onerror={(e) => { if (e.currentTarget instanceof HTMLImageElement) e.currentTarget.src = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23ffffff30' stroke-width='1.5'%3E%3Crect x='3' y='3' width='18' height='18' rx='3'/%3E%3C/svg%3E"; }}
        />
        <span class="text-sm text-foreground font-medium truncate"
          >{c.name}</span
        >
        {#if c.comingSoon}
          <Clock class="size-3.5 text-muted-foreground shrink-0 ml-auto" />
        {:else if isMatch}
          <Check class="size-3.5 text-brand shrink-0 ml-auto" />
        {/if}
      </svelte:element>
    {/each}
  </div>
</div>

<style>
  @keyframes blink {
    0%,
    100% {
      opacity: 1;
    }
    50% {
      opacity: 0;
    }
  }
  .cursor-blink {
    animation: blink 1s steps(2) infinite;
  }
</style>
