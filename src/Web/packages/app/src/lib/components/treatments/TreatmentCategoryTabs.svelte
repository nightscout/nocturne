<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import type {
    EntryCategoryId,
  } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import { Syringe, Utensils, Droplet, FileText, Smartphone, List } from "lucide-svelte";

  interface Props {
    activeCategory: EntryCategoryId | "all";
    categoryCounts: Record<EntryCategoryId | "all", number>;
    onChange: (category: EntryCategoryId | "all") => void;
  }

  let { activeCategory, categoryCounts, onChange }: Props = $props();

  const categoryIcons = {
    bolus: Syringe,
    carbs: Utensils,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
    basalInjection: Syringe,
  } as const;

  function toCategory(value: string): EntryCategoryId | "all" | undefined {
    if (value === "all") return "all";
    return Object.values(ENTRY_CATEGORIES).find((cat) => cat.id === value)?.id;
  }
</script>

<Tabs.Root
  value={activeCategory}
  onValueChange={(v: string) => {
    const category = toCategory(v);
    if (category) onChange(category);
  }}
>
  <!-- eslint-disable shadcn/no-restyle -- the treatment filter is the only tab list drawn as a wrapping grid of tiles, each an icon over its label and count -->
  <Tabs.List
    class="grid h-auto w-full grid-cols-[repeat(auto-fit,minmax(4.5rem,1fr))] gap-2 bg-transparent p-0"
  >
    <Tabs.Trigger
      value="all"
      class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg data-[state=active]:border-primary/30"
    >
      <List class="h-5 w-5" />
      <span class="text-xs font-medium">All</span>
      <Badge variant="secondary" size="sm">
        {categoryCounts.all}
      </Badge>
    </Tabs.Trigger>

    {#each Object.values(ENTRY_CATEGORIES) as cat (cat.id)}
      {@const Icon = categoryIcons[cat.id]}
      <Tabs.Trigger
        value={cat.id}
        class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg data-[state=active]:border-primary/30"
      >
        <Icon class="h-5 w-5 {cat.colorClass}" />
        <span class="text-xs font-medium">{cat.name}</span>
        <Badge variant="secondary" size="sm">
          {categoryCounts[cat.id]}
        </Badge>
      </Tabs.Trigger>
    {/each}
  </Tabs.List>
  <!-- eslint-enable shadcn/no-restyle -->
</Tabs.Root>
