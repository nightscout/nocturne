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
  import PackingSuitcase from "$lib/components/tools/packing/packing-suitcase.svelte";
  import { ConfirmationBackground, type PlayerState } from "@nocturne/watercolour";
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

  let suitcase = $state<PackingSuitcase>();
  let suitcasePaints = $state(true);
  const showSuitcase = $derived(totalCount > 0 && suitcasePaints);

  function followMode(state: PlayerState) {
    suitcasePaints = state.mode !== "none";
  }

  function setPacked(index: number, packed: boolean) {
    const item = items[index];
    if (!item || (item.p === 1) === packed) return;
    items = items.map((it, i) => (i === index ? { ...it, p: packed ? 1 : undefined } : it));
    updateUrl();
    if (packed) suitcase?.pack(item.c, items.filter((it) => !it.p).length);
    else suitcase?.unpack();
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
    const wasPacked = items[index]?.p === 1;
    items = items.filter((_, i) => i !== index);
    updateUrl();
    if (wasPacked) suitcase?.unpack();
    suitcase?.countChanged(items.filter((it) => !it.p).length);
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

<div class="container mx-auto p-6 max-w-2xl lg:max-w-4xl space-y-5">
  <Button variant="ghost" size="sm" href="/tools/packing" class="-ml-2 w-fit">
    <ArrowLeft class="h-4 w-4" />
    Back to calculator
  </Button>

  <!-- The suitcase paints as items are ticked, so the header stays in view for the whole list: a
       full-bleed bar on small screens, a side column with a larger suitcase from lg. -->
  <div class="space-y-5 lg:grid lg:grid-cols-[13rem_minmax(0,1fr)] lg:items-start lg:gap-8 lg:space-y-0">
    <div
      class="sticky top-(--app-sticky-top,0px) z-10 -mx-6 space-y-2 border-b bg-background/95 px-6 py-2 backdrop-blur lg:top-[calc(var(--app-sticky-top,0px)_+_1.5rem)] lg:mx-0 lg:space-y-3 lg:border-b-0 lg:bg-transparent lg:p-0 lg:backdrop-blur-none"
      data-testid="packing-header"
    >
      <div class="relative -mx-3 flex items-center gap-4 rounded-xl px-3 py-1 lg:mx-0 lg:flex-col lg:items-start lg:gap-3 lg:p-0">
        {#if complete}
          <ConfirmationBackground />
        {/if}
        {#if showSuitcase}
          <PackingSuitcase
            bind:this={suitcase}
            packed={items.filter((item) => item.p).map((item) => item.c)}
            total={totalCount}
            onstatechange={followMode}
            class="size-16 shrink-0 lg:size-48 lg:self-center"
          />
        {/if}
        <div class="relative flex flex-1 items-center justify-between gap-2 lg:w-full lg:flex-none lg:flex-col lg:items-start lg:justify-start">
          <h1 class="text-2xl lg:text-xl font-bold tracking-tight flex items-center gap-2">
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
      {#if totalCount > 0}
        <div class="h-2 rounded-full bg-muted overflow-hidden">
          <div
            class="h-full w-(--progress) rounded-full bg-primary transition-all duration-300"
            style:--progress="{(totalChecked / totalCount) * 100}%"
          ></div>
        </div>
      {/if}
    </div>

    <div class="space-y-5">
  {#if items.length === 0}
    <EmptyState art="suitcase" variant="card" title="No items in this list">
      {#snippet action()}
        <Button variant="outline" href="/tools/packing">Go to calculator</Button>
      {/snippet}
    </EmptyState>
  {:else}
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
  </div>
</div>
