<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { DurationInput } from "$lib/components/ui/duration-input";
  import { Plus, Trash2 } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import type { Snippet } from "svelte";

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  type Threshold = Record<string, any>;

  interface Props {
    /** The thresholds array (bindable) */
    thresholds?: Threshold[];
    /** Callback when thresholds change (alternative to binding) */
    onthresholdsChange?: (thresholds: Threshold[]) => void;
    /** Label for the section header */
    label?: string;
    /** Tracker mode for display formatting */
    mode?: "Duration" | "Event";
    /** Lifespan hours for negative threshold validation (Duration mode only) */
    lifespanHours?: number | undefined;
    /** Maximum number of thresholds allowed */
    maxThresholds?: number;
    /** Factory function to create a new threshold */
    createThreshold: () => Threshold;
    /** Field name for hours value (defaults to "hours") */
    hoursField?: string;
    /** Additional CSS classes */
    class?: string;
    /** Snippet for extra columns before hours input */
    extraColumns?: Snippet<[{ threshold: Threshold; index: number; update: (field: string, value: unknown) => void }]>;
    /** Empty state description text */
    emptyDescription?: string;
  }

  let {
    thresholds = $bindable([]),
    onthresholdsChange,
    label = "Thresholds",
    mode = "Duration",
    lifespanHours,
    maxThresholds = 4,
    createThreshold,
    hoursField = "hours",
    class: className,
    extraColumns,
    emptyDescription = "Add thresholds to configure notifications",
  }: Props = $props();

  function setThresholds(newThresholds: Threshold[]) {
    thresholds = newThresholds;
    onthresholdsChange?.(newThresholds);
  }

  function addThreshold() {
    setThresholds([...thresholds, createThreshold()]);
  }

  function removeThreshold(index: number) {
    setThresholds(thresholds.filter((_, i) => i !== index));
  }

  function updateThreshold(index: number, field: string, value: unknown) {
    setThresholds(
      thresholds.map((t, i) => (i === index ? { ...t, [field]: value } : t))
    );
  }

  function getHoursValue(threshold: Threshold): number | undefined {
    return threshold[hoursField] as number | undefined;
  }
</script>

<div class={cn("space-y-3", className)}>
  <div class="flex items-center justify-between">
    <Label class="text-sm font-medium">{label}</Label>
    <Button
      variant="outline"
      size="sm"
      type="button"
      onclick={(e: MouseEvent) => {
        e.stopPropagation();
        addThreshold();
      }}
      disabled={thresholds.length >= maxThresholds}
    >
      <Plus class="h-4 w-4 mr-1" />
      Add
    </Button>
  </div>

  {#if thresholds.length === 0}
    <div
      class="text-center py-4 text-muted-foreground text-sm border border-dashed rounded-lg"
    >
      <p>No thresholds configured</p>
      <p class="text-xs mt-1">{emptyDescription}</p>
    </div>
  {:else}
    <div class="space-y-3">
      {#each thresholds as threshold, i}
        <div class="flex gap-2 items-start p-3 border rounded-lg bg-muted/30">
          {#if extraColumns}
            {@render extraColumns({
              threshold,
              index: i,
              update: (field, value) => updateThreshold(i, field, value)
            })}
          {/if}

          <div class="shrink-0 w-36">
            <Label class="text-xs text-muted-foreground mb-1 block">
              {mode === "Event" ? "Hours" : "After (hours)"}
            </Label>
            <DurationInput
              value={getHoursValue(threshold)}
              onchange={(v) => updateThreshold(i, hoursField, v)}
              placeholder="e.g., 7x24 or -24"
              {mode}
              {lifespanHours}
            />
          </div>

          <div class="flex-1 min-w-0">
            <Label class="text-xs text-muted-foreground mb-1 block">
              Description (optional)
            </Label>
            <Input
              value={threshold.description ?? ""}
              oninput={(e) =>
                updateThreshold(i, "description", e.currentTarget.value)}
              placeholder="Message shown when triggered"
            />
          </div>

          <div class="shrink-0 pt-5">
            <Button
              variant="ghost"
              size="icon"
              type="button"
              class="h-9 w-9 text-muted-foreground hover:text-destructive"
              onclick={(e: MouseEvent) => {
                e.stopPropagation();
                removeThreshold(i);
              }}
            >
              <Trash2 class="h-4 w-4" />
              <span class="sr-only">Remove threshold</span>
            </Button>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  {#if thresholds.length > 0}
    <p class="text-xs text-muted-foreground">
      {#if mode === "Event"}
        Negative = before event, Positive = after event.
      {:else}
        Positive = after start, Negative = before expiration.
      {/if}
    </p>
  {/if}
</div>
