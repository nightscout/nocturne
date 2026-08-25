<script lang="ts">
  import type { Snippet } from "svelte";
  import type { SyncResult } from "$lib/api/generated/nocturne-api-client";
  import { Card, CardContent } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { AlertCircle, CheckCircle } from "lucide-svelte";

  interface Props {
    syncResult: SyncResult | null;
    displayName: string;
    onComplete?: () => void;
    resultActions?: Snippet<[{ result: SyncResult; reset: () => void }]>;
    onReset?: () => void;
  }

  const { syncResult, displayName, onComplete, resultActions, onReset }: Props =
    $props();
</script>

<!--
  Rendered on both outcomes. A failed sync is usually a partial one — the connectors record a count
  per type they reached and fail the run for the type they did not — so on a failure these counts
  are the only thing that says how much of the run still landed.
-->
{#snippet syncedCounts(items: SyncResult["itemsSynced"])}
  {#if items}
    <div class="flex flex-wrap gap-3 mt-3">
      {#each Object.entries(items) as [type, count] (type)}
        {#if count != null}
          <Badge variant="outline">
            {count}
            {type.toLowerCase()}
          </Badge>
        {/if}
      {/each}
    </div>
  {/if}
{/snippet}

<div class="space-y-6">
  {#if syncResult && syncResult.success}
    <Card class="border-green-500">
      <CardContent class="pt-6">
        <div class="flex items-start gap-4">
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
          >
            <CheckCircle class="h-5 w-5 text-green-600 dark:text-green-400" />
          </div>
          <div>
            <p class="font-medium text-lg">Sync completed</p>
            <p class="text-sm text-muted-foreground mt-1">
              {syncResult.message || `${displayName} synced successfully.`}
            </p>
            {@render syncedCounts(syncResult.itemsSynced)}
          </div>
        </div>
      </CardContent>
    </Card>
  {:else if syncResult}
    <Card class="border-destructive">
      <CardContent class="pt-6">
        <div class="flex items-start gap-4">
          <div
            class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30"
          >
            <AlertCircle class="h-5 w-5 text-red-600 dark:text-red-400" />
          </div>
          <div>
            <p class="font-medium text-lg">Sync failed</p>
            <p class="text-sm text-muted-foreground mt-1">
              {syncResult.message || "An error occurred during sync."}
            </p>
            {#if syncResult.errors && syncResult.errors.length > 0}
              <ul class="text-sm text-destructive mt-2 list-disc list-inside">
                {#each syncResult.errors as err}
                  <li>{err}</li>
                {/each}
              </ul>
            {/if}
            {@render syncedCounts(syncResult.itemsSynced)}
          </div>
        </div>
      </CardContent>
    </Card>
  {/if}

  {#if syncResult}
    <div class="flex items-center gap-3">
      {#if onComplete && syncResult.success}
        <Button onclick={onComplete}>Done</Button>
      {/if}
      {#if resultActions}
        {@render resultActions({
          result: syncResult,
          reset: onReset || (() => {}),
        })}
      {/if}
    </div>
  {/if}
</div>
