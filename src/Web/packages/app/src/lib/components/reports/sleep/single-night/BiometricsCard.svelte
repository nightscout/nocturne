<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from "$lib/components/ui/card";
  import { Heart, Activity, Wind, Droplet } from "lucide-svelte";

  interface Props {
    avgHeartRate: number | undefined;
    minHeartRate: number | undefined;
    avgHrv: number | undefined;
    avgBreathRate: number | undefined;
    avgSpo2: number | undefined;
  }

  let { avgHeartRate, minHeartRate, avgHrv, avgBreathRate, avgSpo2 }: Props = $props();

  const rows = $derived(
    [
      { key: "avgHr", label: "Average heart rate", value: avgHeartRate, unit: "bpm", icon: Heart },
      { key: "minHr", label: "Minimum heart rate", value: minHeartRate, unit: "bpm", icon: Heart },
      { key: "hrv", label: "Average HRV", value: avgHrv, unit: "ms", icon: Activity },
      { key: "breath", label: "Average breathing rate", value: avgBreathRate, unit: "breaths/min", icon: Wind },
      { key: "spo2", label: "Average SpO2", value: avgSpo2, unit: "%", icon: Droplet },
    ].filter((row) => row.value != null)
  );
</script>

{#if rows.length > 0}
  <Card class="@container">
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Heart class="h-5 w-5 text-muted-foreground" />
        Biometrics
      </CardTitle>
    </CardHeader>
    <CardContent>
      <div class="grid grid-cols-2 gap-4 @2xl:grid-cols-3">
        {#each rows as row (row.key)}
          <div class="flex items-start gap-2">
            <row.icon class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
            <div>
              <div class="text-xs text-muted-foreground">{row.label}</div>
              <div class="font-medium tabular-nums">
                {Math.round((row.value ?? 0) * 10) / 10} {row.unit}
              </div>
            </div>
          </div>
        {/each}
      </div>
    </CardContent>
  </Card>
{/if}
