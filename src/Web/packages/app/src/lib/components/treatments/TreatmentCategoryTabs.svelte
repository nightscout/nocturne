<script lang="ts">
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Tabs from "#lib/components/ui/tabs/index.js";
  import type {
    EntryCategoryId,
  } from "#lib/constants/entry-categories.js";
  import { ENTRY_CATEGORIES } from "#lib/constants/entry-categories.js";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Utensils from "@lucide/svelte/icons/utensils";
  import Droplet from "@lucide/svelte/icons/droplet";
  import FileText from "@lucide/svelte/icons/file-text";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import List from "@lucide/svelte/icons/list";

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
  <Tabs.List variant="tiles">
    <Tabs.Trigger value="all">
      <List class="h-5 w-5" />
      <span class="text-xs font-medium">All</span>
      <Badge variant="secondary" size="sm">
        {categoryCounts.all}
      </Badge>
    </Tabs.Trigger>

    {#each Object.values(ENTRY_CATEGORIES) as cat (cat.id)}
      {@const Icon = categoryIcons[cat.id]}
      <Tabs.Trigger value={cat.id}>
        <Icon class="h-5 w-5 {cat.colorClass}" />
        <span class="text-center text-xs leading-tight font-medium">{cat.name}</span>
        <Badge variant="secondary" size="sm">
          {categoryCounts[cat.id]}
        </Badge>
      </Tabs.Trigger>
    {/each}
  </Tabs.List>
</Tabs.Root>
