<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Separator } from "$lib/components/ui/separator";
  import ListChecks from "@lucide/svelte/icons/list-checks";
  import Plus from "@lucide/svelte/icons/plus";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import X from "@lucide/svelte/icons/x";
  import { EmptyState } from "$lib/components/shared";
  import { untrack } from "svelte";
  import { Tween, prefersReducedMotion } from "svelte/motion";
  import { cubicOut } from "svelte/easing";
  import {
    Artwork,
    ConfirmationBackground,
    DEFAULT_TAIL,
    type ArtworkPlayer,
    type PlayerState,
  } from "@nocturne/watercolour";
  import { decodeBase64Utf8, encodeBase64Utf8 } from "$lib/utils";

  interface PackingItem {
    c: string; // category
    l: string; // label
    q: number; // quantity
    p?: 1; // packed; carried in the URL so a reload keeps it
  }

  // Decode items from URL
  function decodeItems(): PackingItem[] {
    try {
      const encoded = page.url.searchParams.get("d");
      if (!encoded) return [];
      const decoded: PackingItem[] = JSON.parse(decodeBase64Utf8(decodeURIComponent(encoded)));
      return decoded.map((item) => ({ ...item, p: item.p ? 1 : undefined }));
    } catch {
      return [];
    }
  }

  let items = $state<PackingItem[]>(decodeItems());

  // Group items by category
  const grouped = $derived.by(() => {
    const groups: Record<string, Array<{ item: PackingItem; index: number }>> = {};
    items.forEach((item, index) => {
      if (!groups[item.c]) groups[item.c] = [];
      groups[item.c].push({ item, index });
    });
    return groups;
  });

  const totalChecked = $derived(items.filter((item) => item.p).length);
  const totalCount = $derived(items.length);
  const complete = $derived(totalCount > 0 && totalChecked === totalCount);

  // A catalogue reveal draws every stroke in its first `1 - DEFAULT_TAIL` and
  // spends the rest settling, so packing spreads over the brushwork alone and
  // the settle plays once everything is in.
  const PAINT_END = 1 - DEFAULT_TAIL;
  let suitcase = $state<ArtworkPlayer>();
  let suitcasePaints = $state(true);
  const showSuitcase = $derived(totalCount > 0 && suitcasePaints);
  const reveal = new Tween(0, { easing: cubicOut });

  $effect(() => {
    const target = totalCount ? (totalChecked / totalCount) * PAINT_END : 0;
    // Unpainting is not something paint does, and each backwards frame is a
    // checkpoint replay, so an unpack jumps.
    const instant = prefersReducedMotion.current || target < untrack(() => reveal.target);
    void reveal.set(target, { duration: instant ? 0 : 700 });
  });

  $effect(() => {
    const player = suitcase;
    if (!player) return;
    const at = reveal.current;
    if (!complete || at < PAINT_END) player.seek(at);
    else if (prefersReducedMotion.current) player.finishImmediately();
    else player.play();
  });

  function followPacking(player: ArtworkPlayer) {
    suitcase = player;
    return () => (suitcase = undefined);
  }

  function followMode(state: PlayerState) {
    suitcasePaints = state.mode !== "none";
  }

  function setPacked(index: number, packed: boolean) {
    items = items.map((it, i) => (i === index ? { ...it, p: packed ? 1 : undefined } : it));
    updateUrl();
  }

  // Add custom item
  let addingToCategory = $state<string | null>(null);
  let newLabel = $state("");
  let newQty = $state(1);

  function addItem() {
    if (!newLabel.trim() || !addingToCategory) return;
    items = [
      ...items,
      { c: addingToCategory, l: newLabel.trim(), q: newQty },
    ];
    // Update URL
    updateUrl();
    newLabel = "";
    newQty = 1;
    addingToCategory = null;
  }

  function removeItem(index: number) {
    items = items.filter((_, i) => i !== index);
    updateUrl();
  }

  function updateUrl() {
    const encoded = encodeBase64Utf8(JSON.stringify(items));
    const url = new URL(page.url);
    url.searchParams.set("d", encodeURIComponent(encoded));
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with one param changed, already resolved
    goto(url.toString(), { replaceState: true, noScroll: true });
  }

  function startAdding(category: string) {
    addingToCategory = category;
    newLabel = "";
    newQty = 1;
  }
</script>

<div class="container mx-auto p-6 max-w-2xl space-y-5">
  <!-- Header -->
  <div class="flex flex-col gap-3">
    <Button variant="ghost" size="sm" href="/tools/packing" class="-ml-2 w-fit">
      <ArrowLeft class="h-4 w-4" />
      Back to calculator
    </Button>
    <div class="relative -mx-3 flex items-center gap-4 rounded-xl px-3 py-2" data-testid="packing-header">
      {#if complete}
        <ConfirmationBackground />
      {/if}
      {#if showSuitcase}
        <!-- Reduced motion keeps the baked strip: each seek draws one still frame, so progress shows without animating. -->
        <Artwork
          artwork="suitcase"
          palette="dusk"
          autoplay="never"
          mode={prefersReducedMotion.current ? "baked" : "auto"}
          onready={followPacking}
          onstatechange={followMode}
          class="size-20 shrink-0"
        />
      {/if}
      <div class="relative flex flex-1 items-center justify-between gap-2">
        <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
          {#if !showSuitcase}
            <ListChecks class="h-6 w-6" data-testid="packing-icon" />
          {/if}
          Packing List
        </h1>
        {#if complete}
          <span class="text-sm font-medium">All packed</span>
        {:else if totalCount > 0}
          <span class="text-sm text-muted-foreground tabular-nums">
            {totalChecked}/{totalCount} packed
          </span>
        {/if}
      </div>
    </div>
  </div>

  {#if items.length === 0}
    <EmptyState art="suitcase" variant="card" title="No items in this list">
      {#snippet action()}
        <Button variant="outline" href="/tools/packing">Go to calculator</Button>
      {/snippet}
    </EmptyState>
  {:else}
    <!-- Progress bar -->
    {#if totalCount > 0}
      <div class="h-2 rounded-full bg-muted overflow-hidden">
        <div
          class="h-full w-(--progress) rounded-full bg-primary transition-all duration-300"
          style:--progress="{(totalChecked / totalCount) * 100}%"
        ></div>
      </div>
    {/if}

    <!-- Grouped checklist -->
    {#each Object.entries(grouped) as [category, categoryItems] (category)}
      <Card>
        <CardHeader class="py-3">
          <div class="flex items-center justify-between">
            <CardTitle variant="muted" class="text-sm font-semibold uppercase tracking-wider">
              {category}
            </CardTitle>
            <Button
              variant="ghost"
              size="xs"
              onclick={() => startAdding(category)}
            >
              <Plus class="h-3 w-3 mr-1" />
              Add
            </Button>
          </div>
        </CardHeader>
        <CardContent class="pt-0 pb-2">
          <!-- eslint-disable-next-line svelte/require-each-key -- rows are positional: items carry no id -->
          {#each categoryItems as { item, index }, i}
            {#if i > 0}
              <Separator class="my-0" />
            {/if}
            <div
              class="flex items-center gap-3 py-2.5 group transition-opacity duration-200 {item.p ? 'opacity-40' : ''}"
            >
              <Checkbox
                checked={item.p === 1}
                onCheckedChange={(v: boolean) => setPacked(index, v === true)}
                aria-label="Packed: {item.l}"
              />
              <span
                class="inline-flex items-center rounded-md bg-primary/10 px-2 py-0.5 text-sm font-semibold tabular-nums text-primary shrink-0 {item.p ? 'line-through' : ''}"
              >
                &times;{item.q}
              </span>
              <!-- eslint-disable-next-line no-restricted-syntax -- inline-editable item text -->
              <input
                type="text"
                value={item.l}
                class="text-sm flex-1 bg-transparent border-none outline-none rounded px-1 -mx-1 focus:ring-1 focus:ring-ring {item.p ? 'line-through text-muted-foreground' : ''}"
                onblur={(e) => {
                  const val = e.currentTarget.value.trim();
                  if (val && val !== item.l) {
                    items = items.map((it, idx) => idx === index ? { ...it, l: val } : it);
                    updateUrl();
                  }
                }}
                onkeydown={(e) => { if (e.key === "Enter") e.currentTarget.blur(); }}
              />
              <Button
                variant="ghost-destructive"
                size="icon-2xs"
                reveal
                onclick={() => removeItem(index)}
                aria-label="Remove {item.l}"
              >
                <X />
              </Button>
            </div>
          {/each}

          <!-- Add item form (inline) -->
          {#if addingToCategory === category}
            <Separator class="my-0" />
            <div class="flex items-center gap-2 py-2.5">
              <Input
                type="number"
                bind:value={newQty}
                min={1}
                step={1}
                size="sm"
                class="w-16"
              />
              <Input
                bind:value={newLabel}
                placeholder="Item name..."
                size="sm"
                class="flex-1"
                onkeydown={(e: KeyboardEvent) => e.key === "Enter" && addItem()}
              />
              <Button size="sm" onclick={addItem} disabled={!newLabel.trim()}>
                Add
              </Button>
              <Button
                variant="ghost"
                size="icon-sm"
                onclick={() => (addingToCategory = null)}
              >
                <X class="h-4 w-4" />
              </Button>
            </div>
          {/if}
        </CardContent>
      </Card>
    {/each}

    <!-- Add to new "Custom" category -->
    {#if addingToCategory === "Custom"}
      <Card>
        <CardHeader class="py-3">
          <CardTitle variant="muted" class="text-sm font-semibold uppercase tracking-wider">
            Custom
          </CardTitle>
        </CardHeader>
        <CardContent class="pt-0 pb-2">
          <div class="flex items-center gap-2 py-2.5">
            <Input
              type="number"
              bind:value={newQty}
              min={1}
              step={1}
              size="sm"
              class="w-16"
            />
            <Input
              bind:value={newLabel}
              placeholder="Item name..."
              size="sm"
              class="flex-1"
              onkeydown={(e: KeyboardEvent) => e.key === "Enter" && addItem()}
            />
            <Button size="sm" onclick={addItem} disabled={!newLabel.trim()}>
              Add
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              onclick={() => (addingToCategory = null)}
            >
              <X class="h-4 w-4" />
            </Button>
          </div>
        </CardContent>
      </Card>
    {:else if addingToCategory === null}
      <Button
        variant="outline"
        class="w-full"
        onclick={() => startAdding("Custom")}
      >
        <Plus class="h-4 w-4" />
        Add custom item
      </Button>
    {/if}
  {/if}
</div>
