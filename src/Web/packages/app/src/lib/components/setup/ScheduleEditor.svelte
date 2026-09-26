<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Plus, Trash2 } from "lucide-svelte";

  interface ScheduleEntry {
    time: string;
    value: number;
  }

  interface Props {
    label: string;
    unit: string;
    entries: ScheduleEntry[];
    /** Step size for the value input. E.g. 0.05 for basal, 1 for carb ratio. */
    step?: number;
    min?: number;
  }

  let { label, unit, entries = $bindable(), step = 0.1, min = 0 }: Props = $props();

  function addEntry() {
    entries = [...entries, { time: "00:00", value: 0 }];
  }

  function removeEntry(index: number) {
    entries = entries.filter((_, i) => i !== index);
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <Label>{label}</Label>
    <Button variant="outline" size="sm" onclick={addEntry}>
      <Plus class="h-4 w-4 mr-1" />
      Add
    </Button>
  </div>

  {#if entries.length === 0}
    <p class="text-sm text-muted-foreground">No entries yet. Click Add to create one.</p>
  {:else}
    <div class="space-y-2">
      {#each entries as entry, i (entry)}
        <div class="flex items-center gap-2">
          <Input
            type="time"
            bind:value={entry.time}
            class="w-32"
          />
          <Input
            type="number"
            bind:value={entry.value}
            {step}
            {min}
            class="w-28"
          />
          <span class="text-sm text-muted-foreground whitespace-nowrap">{unit}</span>
          {#if entries.length > 1}
            <Button variant="ghost" size="icon" onclick={() => removeEntry(i)}>
              <Trash2 class="h-4 w-4" />
            </Button>
          {/if}
        </div>
      {/each}
    </div>
  {/if}
</div>
