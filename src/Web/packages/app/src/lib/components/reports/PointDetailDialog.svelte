<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Droplet,
    Pill,
    Cookie,
    Activity,
    Battery,
    Gauge,
    Clock,
    Minus,
    ArrowUp,
    ArrowDown,
    ArrowUpRight,
    ArrowDownRight,
  } from "lucide-svelte";
  import type { PointInTimeData } from "$lib/data/week-to-week.remote";

  interface Props {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    data: PointInTimeData | null;
    loading: boolean;
    dayColor: string;
  }

  let {
    open = $bindable(),
    onOpenChange,
    data,
    loading,
    dayColor,
  }: Props = $props();

  // Get direction icon type
  type DirectionType = "up" | "upRight" | "down" | "downRight" | "flat";
  const directionType = $derived.by((): DirectionType => {
    if (!data?.glucose.direction) return "flat";
    switch (data.glucose.direction) {
      case "DoubleUp":
        return "up";
      case "SingleUp":
        return "upRight";
      case "FortyFiveUp":
        return "upRight";
      case "DoubleDown":
        return "down";
      case "SingleDown":
        return "downRight";
      case "FortyFiveDown":
        return "downRight";
      case "Flat":
        return "flat";
      default:
        return "flat";
    }
  });

  // Format delta
  const deltaDisplay = $derived.by(() => {
    if (data?.glucose.delta === undefined) return null;
    const sign = data.glucose.delta >= 0 ? "+" : "";
    return `${sign}${data.glucose.delta} ${data.glucose.units}`;
  });

  // Get glucose color class based on value
  const glucoseColorClass = $derived.by(() => {
    if (!data) return "";
    const value = data.glucose.value;
    if (value < 70) return "text-red-500";
    if (value < 80) return "text-yellow-500";
    if (value > 250) return "text-red-500";
    if (value > 180) return "text-orange-500";
    return "text-green-500";
  });
</script>

<Dialog.Root bind:open {onOpenChange}>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <div
          class="h-4 w-4 rounded-full"
          style="background-color: {dayColor}"
        ></div>
        Point Details
      </Dialog.Title>
      {#if data}
        <Dialog.Description>
          {data.dayOfWeek} • {data.dateString}
        </Dialog.Description>
      {/if}
    </Dialog.Header>

    {#if loading}
      <div class="flex items-center justify-center py-8">
        <div
          class="animate-spin rounded-full h-8 w-8 border-b-2 border-primary"
        ></div>
      </div>
    {:else if data}
      <div class="space-y-4">
        <!-- Glucose Reading -->
        <div
          class="flex items-center justify-between p-4 rounded-lg bg-muted/50"
        >
          <div class="flex items-center gap-3">
            <Droplet class="h-6 w-6 text-blue-500" />
            <div>
              <div class="text-sm text-muted-foreground">Blood Glucose</div>
              <div class="text-3xl font-bold {glucoseColorClass}">
                {data.glucose.value}
                <span class="text-sm font-normal text-muted-foreground">
                  {data.glucose.units}
                </span>
              </div>
            </div>
          </div>
          <div class="flex flex-col items-end gap-1">
            <div class="flex items-center gap-1 {glucoseColorClass}">
              {#if directionType === "up"}
                <ArrowUp class="h-5 w-5" />
              {:else if directionType === "upRight"}
                <ArrowUpRight class="h-5 w-5" />
              {:else if directionType === "down"}
                <ArrowDown class="h-5 w-5" />
              {:else if directionType === "downRight"}
                <ArrowDownRight class="h-5 w-5" />
              {:else}
                <Minus class="h-5 w-5" />
              {/if}
              {#if data.glucose.direction}
                <span class="text-sm">{data.glucose.direction}</span>
              {/if}
            </div>
            {#if deltaDisplay}
              <Badge variant="outline" class={glucoseColorClass}>
                {deltaDisplay}
              </Badge>
            {/if}
          </div>
        </div>

        <Separator />

        <!-- IOB and COB -->
        <div class="grid grid-cols-2 gap-4">
          <!-- IOB -->
          <div class="p-3 rounded-lg border">
            <div class="flex items-center gap-2 mb-2">
              <Pill class="h-4 w-4 text-purple-500" />
              <span class="text-sm font-medium">IOB</span>
            </div>
            {#if data.iob}
              <div class="text-2xl font-bold">
                {data.iob.value.toFixed(2)} U
              </div>
              <div class="text-xs text-muted-foreground">
                {#if data.iob.basalIob !== undefined}
                  Basal: {data.iob.basalIob.toFixed(2)} U
                {/if}
                {#if data.iob.bolusIob !== undefined}
                  <br />
                  Bolus: {data.iob.bolusIob.toFixed(2)} U
                {/if}
                <br />
                Source: {data.iob.source}
              </div>
            {:else}
              <div class="text-lg text-muted-foreground">—</div>
              <div class="text-xs text-muted-foreground">Not available</div>
            {/if}
          </div>

          <!-- COB -->
          <div class="p-3 rounded-lg border">
            <div class="flex items-center gap-2 mb-2">
              <Cookie class="h-4 w-4 text-orange-500" />
              <span class="text-sm font-medium">COB</span>
            </div>
            {#if data.cob}
              <div class="text-2xl font-bold">
                {data.cob.value.toFixed(0)} g
              </div>
              <div class="text-xs text-muted-foreground">
                Source: {data.cob.source}
              </div>
            {:else}
              <div class="text-lg text-muted-foreground">—</div>
              <div class="text-xs text-muted-foreground">Not available</div>
            {/if}
          </div>
        </div>

        <!-- Recent Treatments -->
        {#if data.recentTreatments.carbs || data.recentTreatments.insulin || data.recentTreatments.bolus || data.recentTreatments.tempBasal}
          <Separator />
          <div class="space-y-2">
            <div class="text-sm font-medium flex items-center gap-2">
              <Activity class="h-4 w-4" />
              Recent Treatments (±15 min)
            </div>
            <div class="grid grid-cols-2 gap-2 text-sm">
              {#if data.recentTreatments.carbs}
                <div
                  class="flex items-center gap-2 p-2 rounded bg-orange-500/10"
                >
                  <Cookie class="h-4 w-4 text-orange-500" />
                  <span>{data.recentTreatments.carbs}g carbs</span>
                </div>
              {/if}
              {#if data.recentTreatments.insulin}
                <div
                  class="flex items-center gap-2 p-2 rounded bg-purple-500/10"
                >
                  <Pill class="h-4 w-4 text-purple-500" />
                  <span>
                    {data.recentTreatments.insulin.toFixed(2)}U insulin
                  </span>
                </div>
              {/if}
              {#if data.recentTreatments.bolus}
                <div class="flex items-center gap-2 p-2 rounded bg-blue-500/10">
                  <Gauge class="h-4 w-4 text-blue-500" />
                  <span>{data.recentTreatments.bolus.toFixed(2)}U bolus</span>
                </div>
              {/if}
              {#if data.recentTreatments.tempBasal}
                <div
                  class="flex items-center gap-2 p-2 rounded bg-green-500/10"
                >
                  <Clock class="h-4 w-4 text-green-500" />
                  <span>
                    Temp basal {data.recentTreatments.tempBasal.rate}U/hr
                  </span>
                </div>
              {/if}
            </div>
          </div>
        {/if}

        <!-- Notes -->
        {#if data.recentTreatments.notes && data.recentTreatments.notes.length > 0}
          <div class="space-y-2">
            <div class="text-sm font-medium">Notes</div>
            {#each data.recentTreatments.notes as note}
              <div
                class="text-sm text-muted-foreground p-2 rounded bg-muted/50"
              >
                {note}
              </div>
            {/each}
          </div>
        {/if}

        <!-- Pump Status -->
        {#if data.pumpStatus}
          <Separator />
          <div class="space-y-2">
            <div class="text-sm font-medium">Pump Status</div>
            <div class="grid grid-cols-3 gap-2 text-sm">
              {#if data.pumpStatus.reservoir !== undefined}
                <div class="text-center p-2 rounded bg-muted/50">
                  <div class="text-lg font-bold">
                    {data.pumpStatus.reservoir}U
                  </div>
                  <div class="text-xs text-muted-foreground">Reservoir</div>
                </div>
              {/if}
              {#if data.pumpStatus.battery !== undefined}
                <div class="text-center p-2 rounded bg-muted/50">
                  <div class="flex items-center justify-center gap-1">
                    <Battery class="h-4 w-4" />
                    <span class="text-lg font-bold">
                      {data.pumpStatus.battery}%
                    </span>
                  </div>
                  <div class="text-xs text-muted-foreground">Battery</div>
                </div>
              {/if}
              {#if data.pumpStatus.status}
                <div class="text-center p-2 rounded bg-muted/50">
                  <div class="text-lg font-bold">{data.pumpStatus.status}</div>
                  <div class="text-xs text-muted-foreground">Status</div>
                </div>
              {/if}
            </div>
          </div>
        {/if}
      </div>
    {:else}
      <div class="flex items-center justify-center py-8 text-muted-foreground">
        No data available for this point
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>
