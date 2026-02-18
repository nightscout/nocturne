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
  } as const;
</script>

<Tabs.Root
  value={activeCategory}
  onValueChange={(v) => onChange(v as EntryCategoryId | "all")}
>
  <Tabs.List
    class="grid w-full h-auto gap-2 bg-transparent p-0"
    style="grid-template-columns: repeat({Object.keys(ENTRY_CATEGORIES).length + 1}, minmax(0, 1fr));"
  >
    <Tabs.Trigger
      value="all"
      class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg border data-[state=active]:border-primary/30"
    >
      <List class="h-5 w-5" />
      <span class="text-xs font-medium">All</span>
      <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
        {categoryCounts.all}
      </Badge>
    </Tabs.Trigger>

    {#each Object.entries(ENTRY_CATEGORIES) as [id, cat]}
      {@const Icon = categoryIcons[id as EntryCategoryId]}
      <Tabs.Trigger
        value={id}
        class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg border data-[state=active]:border-primary/30"
      >
        <Icon class="h-5 w-5 {cat.colorClass}" />
        <span class="text-xs font-medium">{cat.name}</span>
        <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
          {categoryCounts[id as EntryCategoryId]}
        </Badge>
      </Tabs.Trigger>
    {/each}
  </Tabs.List>
</Tabs.Root>
