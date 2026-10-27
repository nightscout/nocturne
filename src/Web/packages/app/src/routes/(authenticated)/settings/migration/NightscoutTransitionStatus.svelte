<script lang="ts">
  import { formatMediumDateTime, formatNumber } from "#lib/utils/formatting.js";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import * as Alert from "#lib/components/ui/alert/index.js";
  import ArrowRightLeft from "@lucide/svelte/icons/arrow-right-left";
  import Activity from "@lucide/svelte/icons/activity";
  import AlertCircle from "@lucide/svelte/icons/circle-alert";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import CheckCircle2 from "@lucide/svelte/icons/circle-check";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Database from "@lucide/svelte/icons/database";
  import ShieldCheck from "@lucide/svelte/icons/shield-check";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import Clock from "@lucide/svelte/icons/clock";
  import BarChart3 from "@lucide/svelte/icons/chart-column";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { databaseArtwork } from "#lib/watercolour-icons.js";
  import { resolve } from "$app/paths";
  import { getTransitionStatus } from "#lib/api/generated/nightscoutTransitions.generated.remote.js";

  const statusQuery = getTransitionStatus();
  const status = $derived(statusQuery.current ?? null);

  function formatTimestamp(ts: Date | string | null | undefined): string {
    if (!ts) return "Never";
    try {
      return formatMediumDateTime(new Date(ts));
    } catch {
      return "Unknown";
    }
  }

  const recommendationColor = $derived.by(() => {
    switch (status?.recommendation?.status) {
      case "safe":
        return "default" as const;
      case "almost-ready":
        return "secondary" as const;
      default:
        return "destructive" as const;
    }
  });

  const recommendationLabel = $derived.by(() => {
    switch (status?.recommendation?.status) {
      case "safe":
        return "Safe to disconnect";
      case "almost-ready":
        return "Almost ready";
      default:
        return "Not ready";
    }
  });

  const compatibilityBadgeVariant = $derived.by(() => {
    const score = status?.compatibility?.compatibilityScore;
    if (score == null) return "secondary" as const;
    if (score >= 95) return "default" as const;
    if (score >= 80) return "secondary" as const;
    return "destructive" as const;
  });

  const compatibilityBadgeLabel = $derived.by(() => {
    const score = status?.compatibility?.compatibilityScore;
    if (score == null) return "No data";
    if (score >= 95) return "High";
    if (score >= 80) return "Medium";
    return "Low";
  });
</script>

{#if statusQuery.loading}
  <div class="flex items-center justify-center py-12">
    <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
  </div>
{:else if statusQuery.error}
  <Alert.Root variant="destructive">
    <AlertTriangle class="h-4 w-4" />
    <Alert.Title>Could not load the Nightscout status</Alert.Title>
    <Alert.Description>Reload the page to try again.</Alert.Description>
  </Alert.Root>
{:else if status}
  <div class="space-y-6">
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <CardTitle class="flex items-center gap-2">
            <Database class="h-5 w-5" />
            Migration status
          </CardTitle>
          <Badge variant={status.migration?.isComplete ? "default" : "secondary"}>
            {#if status.migration?.isComplete}
              <CheckCircle2 class="h-3 w-3" />
              Complete
            {:else}
              <Loader2 class="h-3 w-3 animate-spin" />
              In progress
            {/if}
          </Badge>
        </div>
        <CardDescription>
          The number of records from Nightscout, by data type.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        {#if Object.keys(status.migration?.recordCounts ?? {}).length > 0}
          <div class="grid grid-cols-2 gap-3 @sm:grid-cols-3">
            {#each Object.entries(status.migration?.recordCounts ?? {}) as [dataType, count] (dataType)}
              <div class="rounded-lg border p-3">
                <p class="text-sm text-muted-foreground capitalize">{dataType}</p>
                <p class="text-xl font-semibold tabular-nums">{formatNumber(count)}</p>
              </div>
            {/each}
          </div>
        {:else}
          <EmptyState
            art={databaseArtwork}
            size="compact"
            title="No migration data yet"
          />
        {/if}

        <Separator />

        <div class="flex items-center gap-2 text-sm text-muted-foreground">
          <Clock class="h-4 w-4" />
          <span>Last sync: {formatTimestamp(status.migration?.lastSyncTime)}</span>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <CardTitle class="flex items-center gap-2">
            <Activity class="h-5 w-5" />
            Write-back health (last 24 hours)
          </CardTitle>
          <Badge variant={status.writeBack?.circuitBreakerOpen ? "destructive" : "default"}>
            {#if status.writeBack?.circuitBreakerOpen}
              <ShieldAlert class="h-3 w-3" />
              Paused
            {:else}
              <ShieldCheck class="h-3 w-3" />
              Healthy
            {/if}
          </Badge>
        </div>
        <CardDescription>
          The writes that Nocturne sends back to your Nightscout site.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid grid-cols-3 gap-3">
          <div class="rounded-lg border p-3">
            <p class="text-sm text-muted-foreground">Requests</p>
            <p class="text-xl font-semibold tabular-nums">
              {formatNumber(status.writeBack?.requestsLast24h)}
            </p>
          </div>
          <div class="rounded-lg border p-3">
            <p class="text-sm text-muted-foreground">Succeeded</p>
            <p class="text-xl font-semibold tabular-nums text-success">
              {formatNumber(status.writeBack?.successesLast24h)}
            </p>
          </div>
          <div class="rounded-lg border p-3">
            <p class="text-sm text-muted-foreground">Failed</p>
            <p class="text-xl font-semibold tabular-nums text-destructive">
              {formatNumber(status.writeBack?.failuresLast24h)}
            </p>
          </div>
        </div>

        <Separator />

        <div class="flex items-center gap-2 text-sm text-muted-foreground">
          <Clock class="h-4 w-4" />
          <span>
            Last successful write-back: {formatTimestamp(status.writeBack?.lastSuccessTime)}
          </span>
        </div>
      </CardContent>
    </Card>

    {#if status.compatibility}
      <Card>
        <CardHeader>
          <div class="flex items-center justify-between">
            <CardTitle class="flex items-center gap-2">
              <ShieldCheck class="h-5 w-5" />
              API compatibility
            </CardTitle>
            <Badge variant={compatibilityBadgeVariant}>
              {#if compatibilityBadgeVariant === "default"}
                <CheckCircle2 class="h-3 w-3" />
              {:else if compatibilityBadgeVariant === "destructive"}
                <AlertCircle class="h-3 w-3" />
              {:else}
                <BarChart3 class="h-3 w-3" />
              {/if}
              {compatibilityBadgeLabel}
            </Badge>
          </div>
          <CardDescription>
            Nocturne compares its API responses with the responses from your Nightscout site.
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-4">
          <div class="grid grid-cols-3 gap-3">
            <div class="rounded-lg border p-3">
              <p class="text-sm text-muted-foreground">Score</p>
              <p class="text-2xl font-semibold tabular-nums">
                {status.compatibility.compatibilityScore != null
                  ? `${status.compatibility.compatibilityScore.toFixed(1)}%`
                  : "--"}
              </p>
            </div>
            <div class="rounded-lg border p-3">
              <p class="text-sm text-muted-foreground">Comparisons</p>
              <p class="text-xl font-semibold tabular-nums">
                {formatNumber(status.compatibility.totalComparisons)}
              </p>
            </div>
            <div class="rounded-lg border p-3">
              <p class="text-sm text-muted-foreground">Discrepancies</p>
              <p class="text-xl font-semibold tabular-nums text-destructive">
                {formatNumber(status.compatibility.discrepancies)}
              </p>
            </div>
          </div>
        </CardContent>
      </Card>
    {/if}

    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <ArrowRightLeft class="h-5 w-5" />
          Safe to disconnect
        </CardTitle>
        <CardDescription>
          Shows if you can safely stop using your Nightscout site.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="flex items-center gap-3">
          <Badge variant={recommendationColor} size="lg">
            {#if status.recommendation?.status === "safe"}
              <CheckCircle2 class="h-4 w-4" />
            {:else if status.recommendation?.status === "almost-ready"}
              <Clock class="h-4 w-4" />
            {:else}
              <AlertCircle class="h-4 w-4" />
            {/if}
            {recommendationLabel}
          </Badge>

          {#if status.recommendation?.status === "almost-ready" && status.recommendation?.stabilityDaysRemaining != null}
            <span class="text-sm text-muted-foreground">
              {status.recommendation.stabilityDaysRemaining} stability
              {status.recommendation.stabilityDaysRemaining === 1 ? "day" : "days"} remaining
            </span>
          {/if}
        </div>

        {#if (status.recommendation?.blockers?.length ?? 0) > 0}
          <Separator />
          <div class="space-y-2">
            <p class="text-sm font-medium">Blockers</p>
            <ul class="space-y-1.5">
              {#each status.recommendation?.blockers ?? [] as blocker (blocker)}
                <li class="flex items-start gap-2 text-sm text-muted-foreground">
                  <AlertCircle class="h-4 w-4 mt-0.5 shrink-0 text-destructive" />
                  {blocker}
                </li>
              {/each}
            </ul>
          </div>
        {/if}

        {#if status.recommendation?.status === "safe"}
          <Separator />
          <Button variant="outline" href={resolve("settings/connectors")}>
            Go to connector settings
            <ArrowRightLeft class="h-4 w-4 ml-2" />
          </Button>
        {/if}
      </CardContent>
    </Card>
  </div>
{/if}
