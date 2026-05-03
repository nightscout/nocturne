<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import { getAlertHistory } from "$api/generated/alerts.generated.remote";
  import type { AlertHistoryResponse, HistoryExcursionResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { ArrowLeft, History as HistoryIcon, Loader2 } from "lucide-svelte";
  import { formatDuration, formatDateTime } from "$lib/components/alerts/alertTime";

  let history = $state<AlertHistoryResponse | null>(null);
  let page = $state(1);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load(p: number): Promise<void> {
    loading = true;
    error = null;
    try {
      const r = await getAlertHistory({ page: p, pageSize: 25 });
      history = r ?? null;
      page = p;
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to load history";
    } finally {
      loading = false;
    }
  }

  onMount(() => load(1));
</script>

<svelte:head><title>Alert history · Nocturne</title></svelte:head>

<div class="container mx-auto max-w-4xl p-4 lg:p-6 space-y-6">
  <div class="flex items-center gap-2">
    <Button
      type="button"
      variant="ghost"
      size="icon"
      onclick={() => goto("/alerts")}
      aria-label="Back to alerts"
    >
      <ArrowLeft class="h-4 w-4" />
    </Button>
    <div>
      <h1 class="text-2xl font-bold tracking-tight flex items-center gap-2">
        <HistoryIcon class="h-5 w-5" /> Alert history
      </h1>
      <p class="text-sm text-muted-foreground">Every real fire from this tenant. Test fires aren't shown.</p>
    </div>
  </div>

  {#if error}
    <div class="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive">{error}</div>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle class="text-base">
        Recent fires
        {#if history?.totalCount}
          <span class="text-sm font-normal text-muted-foreground">({history.totalCount} total)</span>
        {/if}
      </CardTitle>
    </CardHeader>
    <CardContent>
      {#if loading}
        <div class="flex items-center justify-center py-8 text-muted-foreground">
          <Loader2 class="h-5 w-5 animate-spin" />
        </div>
      {:else if !history || (history.items ?? []).length === 0}
        <div class="rounded-md border border-dashed py-10 text-center text-muted-foreground">
          <p class="text-sm">No alert history yet.</p>
        </div>
      {:else}
        <div class="space-y-2">
          {#each (history.items ?? []) as h (h.id)}
            {@render historyRow(h)}
          {/each}
        </div>

        {#if (history.totalPages ?? 1) > 1}
          <div class="mt-4 flex items-center justify-between">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={page <= 1 || loading}
              onclick={() => load(page - 1)}
            >
              Previous
            </Button>
            <span class="text-xs text-muted-foreground tabular-nums">
              Page {page} of {history.totalPages}
            </span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={page >= (history.totalPages ?? 1) || loading}
              onclick={() => load(page + 1)}
            >
              Next
            </Button>
          </div>
        {/if}
      {/if}
    </CardContent>
  </Card>
</div>

<!-- Inline row snippet — defined after the markup so the {@render} above sees
     it via Svelte's snippet hoisting. Kept private to this route since it's
     only used here. -->
{#snippet historyRow(h: HistoryExcursionResponse)}
  <div class="flex items-center gap-3 rounded-md border bg-background px-3 py-2">
    <div class="min-w-0 flex-1">
      <div class="flex items-center gap-2">
        <span class="text-sm font-semibold truncate">{h.ruleName ?? "Alert"}</span>
        {#if h.acknowledgedAt}
          <Badge variant="secondary" class="text-[10px]">Acknowledged</Badge>
        {/if}
      </div>
      <div class="text-xs text-muted-foreground">
        {formatDateTime(h.startedAt) || "—"}{#if h.endedAt} → {formatDateTime(h.endedAt) || "—"}{/if} · {formatDuration(h.startedAt, h.endedAt) || "—"}
      </div>
    </div>
  </div>
{/snippet}
