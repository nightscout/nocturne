<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import ArrowLeft from "lucide-svelte/icons/arrow-left";
  import GitCompareArrows from "lucide-svelte/icons/git-compare-arrows";
  import { getCgmComparison, getReportsAnalysis } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import PairedGlucoseScatter from "$lib/components/reports/cgm-comparison/PairedGlucoseScatter.svelte";
  import { bg, bgDelta, bgLabel } from "$lib/utils/formatting";

  const params = requireDateParamsContext(14);

  const resource = contextResource(() => getReportsAnalysis(params.dateRangeInput), {
    errorTitle: "Error Loading CGM Comparison",
  });

  // The unattributed bucket carries no device id, so it can never be a comparison side.
  const devices = $derived(
    (resource.current?.contributingDevices ?? []).filter((d) => d.patientDeviceId)
  );

  let chosenA = $state<string | null>(null);
  let chosenB = $state<string | null>(null);
  let toleranceMinutes = $state(5);

  // A pick is dropped once the device stops contributing to the range, so changing the range
  // can never fire a comparison against a device that has no readings in it.
  const stillContributing = (choice: string | null) =>
    devices.some((d) => d.patientDeviceId === choice) ? choice : null;

  const deviceAId = $derived(stillContributing(chosenA) ?? devices[0]?.patientDeviceId ?? null);
  const deviceBId = $derived(
    stillContributing(chosenB) ??
      devices.find((d) => d.patientDeviceId !== deviceAId)?.patientDeviceId ??
      null
  );

  const nameOf = (id: string | null) =>
    devices.find((d) => d.patientDeviceId === id)?.name ?? "";

  const comparable = $derived(
    devices.length >= 2 && deviceAId !== null && deviceBId !== null && deviceAId !== deviceBId
  );

  const query = $derived(
    comparable
      ? getCgmComparison({
          ...params.dateRangeInput,
          deviceAId: deviceAId!,
          deviceBId: deviceBId!,
          toleranceMinutes,
        })
      : undefined
  );

  const comparison = $derived(query?.current);
  const metrics = $derived(comparison?.metrics);

  const toleranceOptions = [5, 10, 15];

  const percent = (value: number | undefined) =>
    value === undefined ? "-" : `${value.toFixed(1)}%`;
</script>

<svelte:head>
  <title>CGM Comparison - Nocturne Reports</title>
</svelte:head>

{#if resource.current}
  <div class="@container container mx-auto max-w-5xl space-y-6 p-3 @md:p-6">
    <div class="space-y-3">
      <a
        href="/reports/data-quality"
        class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground print:hidden"
      >
        <ArrowLeft class="h-4 w-4" />
        Data Quality
      </a>
      <div class="flex items-center gap-3">
        <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
          <GitCompareArrows class="h-5 w-5 text-primary" />
        </div>
        <div>
          <h1 class="text-2xl font-bold tracking-tight">CGM Comparison</h1>
          <p class="text-muted-foreground">
            Readings from two sensors matched to the same moment
          </p>
        </div>
      </div>
    </div>

    {#if devices.length < 2}
      <Card>
        <CardContent class="pt-6 text-sm text-muted-foreground">
          Two registered CGMs need readings in this range to compare. This range has {devices.length}.
        </CardContent>
      </Card>
    {:else}
      <Card class="print:hidden">
        <CardHeader class="pb-3">
          <CardTitle class="text-base">Devices</CardTitle>
          <CardDescription>
            Relative figures are measured against the reference device.
          </CardDescription>
        </CardHeader>
        <CardContent class="flex flex-wrap items-end gap-4">
          <div class="flex min-w-48 flex-col gap-1">
            <label for="cgm-a" class="text-sm text-muted-foreground">Device</label>
            <Select.Root type="single" value={deviceAId ?? ""} onValueChange={(v) => (chosenA = v)}>
              <Select.Trigger id="cgm-a" class="w-full">{nameOf(deviceAId)}</Select.Trigger>
              <Select.Content>
                {#each devices as device (device.patientDeviceId)}
                  <Select.Item value={device.patientDeviceId!} label={device.name ?? ""} />
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="flex min-w-48 flex-col gap-1">
            <label for="cgm-b" class="text-sm text-muted-foreground">Reference</label>
            <Select.Root type="single" value={deviceBId ?? ""} onValueChange={(v) => (chosenB = v)}>
              <Select.Trigger id="cgm-b" class="w-full">{nameOf(deviceBId)}</Select.Trigger>
              <Select.Content>
                {#each devices as device (device.patientDeviceId)}
                  <Select.Item value={device.patientDeviceId!} label={device.name ?? ""} />
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="flex min-w-40 flex-col gap-1">
            <label for="cgm-tolerance" class="text-sm text-muted-foreground">Match within</label>
            <Select.Root
              type="single"
              value={String(toleranceMinutes)}
              onValueChange={(v) => (toleranceMinutes = Number(v))}
            >
              <Select.Trigger id="cgm-tolerance" class="w-full">
                {toleranceMinutes} minutes
              </Select.Trigger>
              <Select.Content>
                {#each toleranceOptions as option (option)}
                  <Select.Item value={String(option)} label="{option} minutes" />
                {/each}
              </Select.Content>
            </Select.Root>
          </div>
        </CardContent>
      </Card>

      {#if !comparable}
        <Card>
          <CardContent class="pt-6 text-sm text-muted-foreground">
            Pick two different devices to compare.
          </CardContent>
        </Card>
      {:else if query?.error}
        <Card>
          <CardContent class="pt-6 text-sm text-muted-foreground">
            The comparison could not be loaded.
          </CardContent>
        </Card>
      {:else if comparison}
        <Card>
          <CardHeader class="pb-3">
            <CardTitle class="text-base">Agreement</CardTitle>
            <CardDescription>
              {comparison.deviceAName} against {comparison.deviceBName}, readings matched within
              {comparison.toleranceMinutes} minutes.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if metrics}
              <dl class="grid grid-cols-2 gap-4 @md:grid-cols-5">
                <div>
                  <dt class="text-xs text-muted-foreground">Paired readings</dt>
                  <dd class="text-xl font-semibold tabular-nums">{metrics.pairCount}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">
                    Mean absolute difference ({bgLabel()})
                  </dt>
                  <dd class="text-xl font-semibold tabular-nums">
                    {bg(metrics.meanAbsoluteDifferenceMgdl ?? 0)}
                  </dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">MARD</dt>
                  <dd class="text-xl font-semibold tabular-nums">{percent(metrics.mardPercent)}</dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">Bias ({bgLabel()})</dt>
                  <dd class="text-xl font-semibold tabular-nums">
                    {bgDelta(metrics.biasMgdl ?? 0)}
                  </dd>
                </div>
                <div>
                  <dt class="text-xs text-muted-foreground">
                    Within {bg(15)} {bgLabel()} or 15%
                  </dt>
                  <dd class="text-xl font-semibold tabular-nums">
                    {percent(metrics.within15Percent)}
                  </dd>
                </div>
              </dl>
            {:else}
              <p class="text-sm text-muted-foreground">
                No paired readings in this range at this tolerance.
              </p>
            {/if}
            <p class="mt-4 text-xs text-muted-foreground">
              {comparison.deviceAName}: {comparison.readingCountA} readings, {comparison.unpairedCountA}
              unpaired. {comparison.deviceBName}: {comparison.readingCountB} readings,
              {comparison.unpairedCountB} unpaired.
            </p>
          </CardContent>
        </Card>

        {#if metrics}
          <Card>
            <CardHeader class="pb-3">
              <CardTitle class="text-base">Paired readings</CardTitle>
              <CardDescription>
                Each point is one matched pair; the dashed line is where the two read the same
                value.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <PairedGlucoseScatter
                pairs={comparison.pairs ?? []}
                nameA={comparison.deviceAName ?? ""}
                nameB={comparison.deviceBName ?? ""}
              />
            </CardContent>
          </Card>
        {/if}
      {/if}
    {/if}
  </div>
{/if}
