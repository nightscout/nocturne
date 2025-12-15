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
  import { getPredictions } from "$lib/data/predictions.remote";

  // Prediction display mode type
  export type PredictionDisplayMode =
    | "cone"
    | "lines"
    | "main"
    | "iob"
    | "zt"
    | "uam"
    | "cob";

  interface Props {
    /** Whether to show prediction controls (from parent) */
    showPredictions?: boolean;
    /** Current prediction display mode (bindable) */
    predictionMode?: PredictionDisplayMode;
    /** Algorithm setting for prediction model */
    predictionModel?: string;
  }

  let {
    showPredictions = true,
    predictionMode = $bindable("cone"),
    predictionModel = "cone",
  }: Props = $props();

  // Sync prediction mode with algorithm settings model on mount
  $effect(() => {
    const modelToMode: Record<string, PredictionDisplayMode> = {
      ar2: "cone",
      linear: "cone",
      iob: "iob",
      cob: "cob",
      uam: "uam",
      cone: "cone",
      lines: "lines",
    };
    predictionMode = modelToMode[predictionModel] ?? "cone";
  });

  // Fetch predictions - this triggers the svelte:boundary states
  const predictionsQuery = $derived(
    showPredictions && predictionEnabled.current ? getPredictions({}) : null
  );
  // Await to trigger boundary states - the result itself isn't used here,
  // just validates that predictions are available
  const _predictionStatus = $derived(
    predictionsQuery ? await predictionsQuery : null
  );
  // Reference in effect to suppress unused warning and ensure reactivity
  $effect(() => {
    void _predictionStatus;
  });
</script>

<svelte:boundary>
  {#snippet pending()}
    <!-- Loading state skeleton -->
    <div class="flex items-center gap-2">
      <!-- Skeleton for mode selector -->
      <div class="bg-slate-900 rounded-lg p-0.5 flex gap-0.5">
        {#each [1, 2, 3, 4, 5] as _}
          <Skeleton class="h-6 w-10 rounded-md" />
        {/each}
      </div>
      <!-- Skeleton for time selector -->
      <div class="bg-slate-900 rounded-lg p-0.5">
        <Skeleton class="h-7 w-[90px] rounded-md" />
      </div>
    </div>
  {/snippet}

  {#snippet failed(error, reset)}
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
        bind:value={predictionMode}
        class="bg-slate-900 rounded-lg p-0.5"
      >
        <ToggleGroup.Item
          value="cone"
          class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-purple-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
          title="Cone of probabilities"
        >
          Cone
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="lines"
          class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-purple-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
          title="All prediction lines"
        >
          Lines
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="iob"
          class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-cyan-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
          title="IOB only"
        >
          IOB
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="zt"
          class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-orange-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
          title="Zero Temp"
        >
          ZT
        </ToggleGroup.Item>
        <ToggleGroup.Item
          value="uam"
          class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-green-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
          title="UAM"
        >
          UAM
        </ToggleGroup.Item>
      </ToggleGroup.Root>
    {/if}

    <!-- Prediction time/enable selector -->
    {#if showPredictions}
      <div class="bg-slate-900 rounded-lg p-0.5">
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
            class="h-7 w-[90px] bg-transparent border-none text-xs text-slate-400 focus:ring-0 focus:ring-offset-0 px-2 data-[placeholder]:text-slate-400"
          >
            <div class="flex items-center gap-1.5 truncate">
              {#if !predictionEnabled.current}
                <span class="text-slate-500">Off</span>
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
