<script lang="ts">
  import type { ApsSnapshot } from "$lib/api";
  import * as Card from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
  import {
    Brain,
    Syringe,
    Apple,
    Target,
    Check,
    Minus,
  } from "lucide-svelte";

  interface Props {
    snapshot: ApsSnapshot | null;
  }

  let { snapshot }: Props = $props();

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  const snapshotTime = $derived(
    snapshot?.mills
      ? new Date(snapshot.mills).toLocaleTimeString(undefined, {
          hour: "2-digit",
          minute: "2-digit",
        })
      : null
  );

  const isEnacted = $derived(snapshot?.enacted ?? false);

  const totalIob = $derived(snapshot?.iob ?? 0);
  const basalIob = $derived(snapshot?.basalIob);
  const bolusIob = $derived(snapshot?.bolusIob);
</script>

{#if snapshot}
  <Card.Root class="@container">
    <Card.Header class="pb-2">
      <div class="flex flex-col gap-3 @lg:flex-row @lg:items-center @lg:justify-between">
        <Card.Title class="flex items-center gap-2 text-sm">
          <Brain class="h-4 w-4" />
          APS Decision at {snapshotTime}
        </Card.Title>
        <Badge
          variant={isEnacted ? "default" : "outline"}
          class={isEnacted
            ? "shrink-0 bg-green-600/20 text-green-400 border-green-600/40"
            : "shrink-0"}
        >
          {#if isEnacted}
            <Check class="h-3 w-3 mr-1" />
            Enacted
          {:else}
            Suggested
          {/if}
        </Badge>
      </div>
    </Card.Header>
    <Card.Content>
      <div class="grid grid-cols-2 @sm:grid-cols-4 gap-x-6 gap-y-3 text-sm">
        <!-- IOB -->
        <div>
          <div class="flex items-center gap-1 text-muted-foreground">
            <Syringe class="h-3 w-3" />
            IOB
          </div>
          <div class="font-medium tabular-nums">
            {totalIob.toFixed(2)}U
          </div>
          {#if basalIob != null && bolusIob != null}
            <div class="text-xs text-muted-foreground tabular-nums">
              {basalIob.toFixed(2)} basal / {bolusIob.toFixed(2)} bolus
            </div>
          {/if}
        </div>

        <!-- COB -->
        <div>
          <div class="flex items-center gap-1 text-muted-foreground">
            <Apple class="h-3 w-3" />
            COB
          </div>
          <div class="font-medium tabular-nums">
            {(snapshot.cob ?? 0).toFixed(0)}g
          </div>
        </div>

        <!-- Current BG -->
        <div>
          <div class="text-muted-foreground">Current BG</div>
          <div class="font-medium tabular-nums">
            {#if snapshot.currentBg}
              {formatGlucoseValue(snapshot.currentBg, units)} {unitLabel}
            {:else}
              <Minus class="h-4 w-4 text-muted-foreground" />
            {/if}
          </div>
        </div>

        <!-- Eventual BG -->
        <div>
          <div class="flex items-center gap-1 text-muted-foreground">
            <Target class="h-3 w-3" />
            Eventual BG
          </div>
          <div class="font-medium tabular-nums">
            {#if snapshot.eventualBg}
              {formatGlucoseValue(snapshot.eventualBg, units)} {unitLabel}
            {:else}
              <Minus class="h-4 w-4 text-muted-foreground" />
            {/if}
          </div>
        </div>

        <!-- Target BG -->
        <div>
          <div class="text-muted-foreground">Target</div>
          <div class="font-medium tabular-nums">
            {#if snapshot.targetBg}
              {formatGlucoseValue(snapshot.targetBg, units)} {unitLabel}
            {:else}
              <Minus class="h-4 w-4 text-muted-foreground" />
            {/if}
          </div>
        </div>

        <!-- Sensitivity -->
        <div>
          <div class="text-muted-foreground">Sensitivity</div>
          <div class="font-medium tabular-nums">
            {#if snapshot.sensitivityRatio}
              {(snapshot.sensitivityRatio * 100).toFixed(0)}%
            {:else}
              <Minus class="h-4 w-4 text-muted-foreground" />
            {/if}
          </div>
        </div>

        <!-- Enacted Action -->
        {#if isEnacted}
          <div>
            <div class="text-muted-foreground">Temp Basal</div>
            <div class="font-medium tabular-nums">
              {#if snapshot.enactedRate != null}
                {snapshot.enactedRate.toFixed(2)}U/hr
                {#if snapshot.enactedDuration}
                  <span class="text-xs text-muted-foreground">
                    ({snapshot.enactedDuration}m)
                  </span>
                {/if}
              {:else}
                <Minus class="h-4 w-4 text-muted-foreground" />
              {/if}
            </div>
          </div>
          {#if snapshot.enactedBolusVolume && snapshot.enactedBolusVolume > 0}
            <div>
              <div class="text-muted-foreground">Auto-bolus</div>
              <div class="font-medium tabular-nums">
                {snapshot.enactedBolusVolume.toFixed(2)}U
              </div>
            </div>
          {/if}
        {:else if snapshot.recommendedBolus != null && snapshot.recommendedBolus > 0}
          <div>
            <div class="text-muted-foreground">Recommended</div>
            <div class="font-medium tabular-nums">
              {snapshot.recommendedBolus.toFixed(2)}U
            </div>
          </div>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>
{/if}
