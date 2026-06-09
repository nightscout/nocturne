<script lang="ts">
  import { CalendarDays, Filter, SlidersHorizontal } from "lucide-svelte";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";

  let {
    availableDataSources,
    selectedDataSources = $bindable([]),
    presentDataTypes,
    hiddenDataTypes,
    toggleDataType,
    showAllDataTypes,
  } = $props<{
    availableDataSources: string[];
    selectedDataSources: string[];
    presentDataTypes: string[];
    hiddenDataTypes: Set<string>;
    toggleDataType: (type: string) => void;
    showAllDataTypes: () => void;
  }>();
</script>

<div class="@container">
<div
  class="mb-6 flex flex-col gap-4 @sm:flex-row @sm:items-center @sm:justify-between"
>
  <div class="flex items-center gap-3">
    <div
      class="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10"
    >
      <CalendarDays class="h-5 w-5 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Year Overview</h1>
      <p class="text-sm text-muted-foreground">
        Multi-year heatmap of all your data
      </p>
    </div>
  </div>

  <!-- Filters -->
  <div class="flex items-center gap-2">
    <!-- Data Source Filter (multi-select) -->
    <div class="flex items-center gap-2">
      <Filter class="h-4 w-4 text-muted-foreground" />
      <Select.Root
        type="multiple"
        value={selectedDataSources}
        onValueChange={(v) => {
          selectedDataSources = v ?? [];
        }}
        disabled={availableDataSources.length === 0}
      >
        <Select.Trigger class="w-[200px]">
          <span class="truncate">
            {selectedDataSources.length === 0
              ? "All Data Sources"
              : selectedDataSources.length === 1
                ? getDataTypeLabel(selectedDataSources[0])
                : `${selectedDataSources.length} sources`}
          </span>
        </Select.Trigger>
        <Select.Content>
          {#each availableDataSources as source}
            <Select.Item value={source}>
              {getDataTypeLabel(source)}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>

    <!-- Data Type Filter -->
    {#if presentDataTypes.length > 0}
      <Popover.Root>
        <Popover.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button variant="outline" size="sm" class="gap-1.5" {...props}>
              <SlidersHorizontal class="h-3.5 w-3.5" />
              Types
              {#if hiddenDataTypes.size > 0}
                <span
                  class="ml-1 rounded-full bg-primary px-1.5 py-0.5 text-[10px] font-medium leading-none text-primary-foreground"
                >
                  {presentDataTypes.length -
                    hiddenDataTypes.size}/{presentDataTypes.length}
                </span>
              {/if}
            </Button>
          {/snippet}
        </Popover.Trigger>
        <Popover.Content class="w-56 p-3" align="end">
          <div class="mb-2 flex items-center justify-between">
            <span class="text-xs font-medium text-muted-foreground">
              Show data types
            </span>
            {#if hiddenDataTypes.size > 0}
              <button
                class="text-xs text-primary hover:underline"
                onclick={showAllDataTypes}
              >
                Show all
              </button>
            {/if}
          </div>
          <div class="space-y-1.5">
            {#each presentDataTypes as dataType}
              <label
                class="flex cursor-pointer items-center gap-2 rounded px-1 py-0.5 text-sm hover:bg-muted/50"
              >
                <Checkbox
                  checked={!hiddenDataTypes.has(dataType)}
                  onCheckedChange={() => toggleDataType(dataType)}
                />
                {getDataTypeLabel(dataType)}
              </label>
            {/each}
          </div>
        </Popover.Content>
      </Popover.Root>
    {/if}
  </div>
</div>
</div>
