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
  import { ListChecks, Plus, ArrowLeft, X } from "lucide-svelte";

  interface PackingItem {
    c: string; // category
    l: string; // label
    q: number; // quantity
  }

  // Decode items from URL
  function decodeItems(): PackingItem[] {
    try {
      const encoded = page.url.searchParams.get("d");
      if (!encoded) return [];
      return JSON.parse(atob(decodeURIComponent(encoded)));
    } catch {
      return [];
    }
  }

  let items = $state<PackingItem[]>(decodeItems());
  let checked = $state<Record<number, boolean>>({});

  // Group items by category
  const grouped = $derived.by(() => {
    const groups: Record<string, Array<{ item: PackingItem; index: number }>> = {};
    items.forEach((item, index) => {
      if (!groups[item.c]) groups[item.c] = [];
      groups[item.c].push({ item, index });
    });
    return groups;
  });

  const totalChecked = $derived(Object.values(checked).filter(Boolean).length);
  const totalCount = $derived(items.length);

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
    // Shift checked states
    const newChecked: Record<number, boolean> = {};
    Object.entries(checked).forEach(([k, v]) => {
      const ki = parseInt(k);
      if (ki < index) newChecked[ki] = v;
      else if (ki > index) newChecked[ki - 1] = v;
    });
    checked = newChecked;
    updateUrl();
  }

  function updateUrl() {
    const encoded = btoa(JSON.stringify(items));
    const url = new URL(page.url);
    url.searchParams.set("d", encodeURIComponent(encoded));
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
    <Button variant="ghost" size="sm" href="/tools/packing" class="gap-1 -ml-2 w-fit">
      <ArrowLeft class="h-4 w-4" />
      Back to calculator
    </Button>
    <div class="flex items-center justify-between">
      <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
        <ListChecks class="h-6 w-6" />
        Packing List
      </h1>
      {#if totalCount > 0}
        <span class="text-sm text-muted-foreground tabular-nums">
          {totalChecked}/{totalCount} packed
        </span>
      {/if}
    </div>
  </div>

  {#if items.length === 0}
    <Card>
      <CardContent class="pt-6 text-center">
        <p class="text-muted-foreground">No items in this list.</p>
        <Button variant="outline" href="/tools/packing" class="mt-4">
          Go to calculator
        </Button>
      </CardContent>
    </Card>
  {:else}
    <!-- Progress bar -->
    {#if totalCount > 0}
      <div class="h-2 rounded-full bg-muted overflow-hidden">
        <div
          class="h-full rounded-full bg-primary transition-all duration-300"
          style="width: {(totalChecked / totalCount) * 100}%"
        ></div>
      </div>
    {/if}

    <!-- Grouped checklist -->
    {#each Object.entries(grouped) as [category, categoryItems]}
      <Card>
        <CardHeader class="py-3">
          <div class="flex items-center justify-between">
            <CardTitle class="text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              {category}
            </CardTitle>
            <Button
              variant="ghost"
              size="sm"
              class="h-7 px-2 text-xs"
              onclick={() => startAdding(category)}
            >
              <Plus class="h-3 w-3 mr-1" />
              Add
            </Button>
          </div>
        </CardHeader>
        <CardContent class="pt-0 pb-2">
          {#each categoryItems as { item, index }, i}
            {#if i > 0}
              <Separator class="my-0" />
            {/if}
            <div
              class="flex items-center gap-3 py-2.5 group transition-opacity duration-200 {checked[index] ? 'opacity-40' : ''}"
            >
              <Checkbox
                checked={checked[index] ?? false}
                onCheckedChange={(v: boolean) => (checked[index] = v === true)}
              />
              <span
                class="inline-flex items-center rounded-md bg-primary/10 px-2 py-0.5 text-sm font-semibold tabular-nums text-primary shrink-0 {checked[index] ? 'line-through' : ''}"
              >
                &times;{item.q}
              </span>
              <input
                type="text"
                value={item.l}
                class="text-sm flex-1 bg-transparent border-none outline-none rounded px-1 -mx-1 focus:ring-1 focus:ring-ring {checked[index] ? 'line-through text-muted-foreground' : ''}"
                onblur={(e) => {
                  const val = e.currentTarget.value.trim();
                  if (val && val !== item.l) {
                    items = items.map((it, idx) => idx === index ? { ...it, l: val } : it);
                    updateUrl();
                  }
                }}
                onkeydown={(e) => { if (e.key === "Enter") e.currentTarget.blur(); }}
              />
              <button
                type="button"
                class="opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded hover:bg-destructive/10"
                onclick={() => removeItem(index)}
              >
                <X class="h-3.5 w-3.5 text-muted-foreground hover:text-destructive" />
              </button>
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
                class="w-16 h-8 text-xs"
              />
              <Input
                bind:value={newLabel}
                placeholder="Item name..."
                class="flex-1 h-8 text-sm"
                onkeydown={(e: KeyboardEvent) => e.key === "Enter" && addItem()}
              />
              <Button size="sm" class="h-8" onclick={addItem} disabled={!newLabel.trim()}>
                Add
              </Button>
              <Button
                variant="ghost"
                size="sm"
                class="h-8 px-2"
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
          <CardTitle class="text-sm font-semibold text-muted-foreground uppercase tracking-wider">
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
              class="w-16 h-8 text-xs"
            />
            <Input
              bind:value={newLabel}
              placeholder="Item name..."
              class="flex-1 h-8 text-sm"
              onkeydown={(e: KeyboardEvent) => e.key === "Enter" && addItem()}
            />
            <Button size="sm" class="h-8" onclick={addItem} disabled={!newLabel.trim()}>
              Add
            </Button>
            <Button
              variant="ghost"
              size="sm"
              class="h-8 px-2"
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
        class="w-full gap-2"
        onclick={() => startAdding("Custom")}
      >
        <Plus class="h-4 w-4" />
        Add custom item
      </Button>
    {/if}
  {/if}
</div>
