<script lang="ts">
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import { AlertCircle, RefreshCw } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import {
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";

  import type { PredictionDisplayMode } from "$lib/stores/appearance-store.svelte";

  interface Props {
    /** Whether to show prediction controls (from parent) */
    showPredictions?: boolean;
    /** Current prediction display mode */
    predictionMode?: PredictionDisplayMode;
    /** Callback when prediction mode changes */
    onPredictionModeChange?: (mode: PredictionDisplayMode) => void;
  }

  let {
    showPredictions = true,
    predictionMode = "cone",
    onPredictionModeChange,
  }: Props = $props();

  // Handle mode changes via callback
  function handleModeChange(value: string | undefined) {
    if (value && onPredictionModeChange) {
      onPredictionModeChange(value as PredictionDisplayMode);
    }
  }

  // Sync prediction mode with algorithm settings model on mount
</script>

<svelte:boundary>
  {#snippet pending()}
    <!-- Loading state skeleton -->
    <div class="flex items-center gap-2">
      <!-- Skeleton for mode selector -->
      <div class="bg-muted rounded-lg p-0.5 flex gap-0.5">
        {#each [1, 2, 3, 4, 5] as _}
          <Skeleton class="h-6 w-10 rounded-md" />
        {/each}
      </div>
      <!-- Skeleton for time selector -->
      <div class="bg-muted rounded-lg p-0.5">
        <Skeleton class="h-7 w-[90px] rounded-md" />
      </div>
    </div>
  {/snippet}

  {#snippet failed(_error, reset)}
    <!-- Error state with retry -->
    <div class="flex items-center gap-2 text-xs">
      <AlertCircle class="h-4 w-4 text-destructive" />
      <span class="text-destructive">Predictions unavailable</span>
      <Button
        variant="ghost"
        size="sm"
        class="h-6 px-2 text-xs"
        onclick={reset}
      >
        <RefreshCw class="h-3 w-3 mr-1" />
        Retry
      </Button>
    </div>
  {/snippet}

  <!-- Normal prediction controls -->
  <div class="flex items-center gap-2">
    <!-- Prediction mode selector -->
    <!-- Only show mode selector if predictions are enabled in settings/store AND prop overrides -->
    {#if showPredictions && predictionEnabled.current}
      <ToggleGroup.Root
        type="single"
        value={predictionMode}
        onValueChange={handleModeChange}
        class="bg-muted rounded-lg p-0.5"
      >
        <ToggleGroup.Item
          value="cone"
          class="px-2 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-purple-700 data-[state=on]:text-white rounded-md transition-colors"
          title="Cone of probabilities"
        >
          Cone
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="lines"
          class="px-2 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-purple-700 data-[state=on]:text-white rounded-md transition-colors"
          title="All prediction lines"
        >
          Lines
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="iob"
          class="px-2 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-cyan-700 data-[state=on]:text-white rounded-md transition-colors"
          title="IOB only"
        >
          IOB
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="zt"
          class="px-2 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-orange-700 data-[state=on]:text-white rounded-md transition-colors"
          title="Zero Temp"
        >
          ZT
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="uam"
          class="px-2 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-green-700 data-[state=on]:text-white rounded-md transition-colors"
          title="UAM"
        >
          UAM
        </ToggleGroup.Item>
      </ToggleGroup.Root>
    {/if}

    <!-- Prediction time/enable selector -->
    {#if showPredictions}
      <div class="bg-muted rounded-lg p-0.5">
        <Select
          type="single"
          value={predictionEnabled.current
            ? predictionMinutes.current.toString()
            : "disabled"}
          onValueChange={(v) => {
            if (v === "disabled") {
              predictionEnabled.current = false;
            } else {
              predictionEnabled.current = true;
              predictionMinutes.current = parseInt(v);
            }
          }}
        >
          <SelectTrigger
            class="h-7 w-[90px] bg-transparent border-none text-xs text-muted-foreground focus:ring-0 focus:ring-offset-0 px-2 data-[placeholder]:text-muted-foreground"
          >
            <div class="flex items-center gap-1.5 truncate">
              {#if !predictionEnabled.current}
                <span class="text-muted-foreground">Off</span>
              {:else}
                <span>
                  {predictionMinutes.current < 60
                    ? `${predictionMinutes.current}m`
                    : `${predictionMinutes.current / 60}h`}
                </span>
              {/if}
            </div>
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="disabled" class="text-xs text-muted-foreground">
              Disable
            </SelectItem>
            <SelectItem value="15" class="text-xs">15 min</SelectItem>
            <SelectItem value="30" class="text-xs">30 min</SelectItem>
            <SelectItem value="60" class="text-xs">1 hour</SelectItem>
            <SelectItem value="120" class="text-xs">2 hours</SelectItem>
            <SelectItem value="180" class="text-xs">3 hours</SelectItem>
            <SelectItem value="240" class="text-xs">4 hours</SelectItem>
          </SelectContent>
        </Select>
      </div>
    {/if}
  </div>
</svelte:boundary>
