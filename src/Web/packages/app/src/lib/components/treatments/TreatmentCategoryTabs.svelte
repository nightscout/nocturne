<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import * as Tabs from "$lib/components/ui/tabs";
  import type {
    V4CategoryId,
    V4TreatmentCounts,
  } from "$lib/constants/treatment-categories";
  import { Syringe, Utensils, List } from "lucide-svelte";

  interface Props {
    activeCategory: V4CategoryId | "all";
    categoryCounts: V4TreatmentCounts;
    onChange: (category: V4CategoryId | "all") => void;
  }

  let { activeCategory, categoryCounts, onChange }: Props = $props();
</script>

<Tabs.Root
  value={activeCategory}
  onValueChange={(v) => onChange(v as V4CategoryId | "all")}
>
  <Tabs.List
    class="grid w-full grid-cols-3 h-auto gap-2 bg-transparent p-0"
  >
    <Tabs.Trigger
      value="all"
      class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg border data-[state=active]:border-primary/30"
    >
      <List class="h-5 w-5" />
      <span class="text-xs font-medium">All</span>
      <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
        {categoryCounts.total}
      </Badge>
    </Tabs.Trigger>

    <Tabs.Trigger
      value="bolus"
      class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg border data-[state=active]:border-primary/30"
    >
      <Syringe class="h-5 w-5 text-blue-600 dark:text-blue-400" />
      <span class="text-xs font-medium">Bolus</span>
      <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
        {categoryCounts.bolus}
      </Badge>
    </Tabs.Trigger>

    <Tabs.Trigger
      value="carbs"
      class="flex flex-col items-center gap-1 p-3 data-[state=active]:bg-primary/10 data-[state=active]:text-primary rounded-lg border data-[state=active]:border-primary/30"
    >
      <Utensils class="h-5 w-5 text-green-600 dark:text-green-400" />
      <span class="text-xs font-medium">Carbs</span>
      <Badge variant="secondary" class="text-[10px] px-1.5 py-0">
        {categoryCounts.carbs}
      </Badge>
    </Tabs.Trigger>
  </Tabs.List>
</Tabs.Root>
