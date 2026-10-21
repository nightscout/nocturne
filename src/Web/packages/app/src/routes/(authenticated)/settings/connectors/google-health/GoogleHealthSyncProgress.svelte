<script lang="ts">
  import type { GoogleHealthStatus } from "$lib/api";
  import { Progress } from "$lib/components/ui/progress";
  import { lastSeen } from "$lib/utils/formatting";
  import { syncPhaseLabel } from "./google-health-copy.svelte";

  let { status }: { status: GoogleHealthStatus } = $props();
  const percent = $derived(status.syncProgressPercent ?? 0);
</script>

<div
  role="status"
  aria-live="polite"
  class="space-y-3 rounded-lg border border-primary/40 bg-primary/5 p-4"
>
  <div>
    <p class="font-medium">Import running in the background</p>
    <p class="text-sm text-muted-foreground">{syncPhaseLabel(status)}</p>
  </div>
  <Progress value={percent} max={100} aria-label="Google Health import progress" />
  <div class="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted-foreground">
    <span>{percent}% complete</span>
    {#if (status.syncTotalDataTypes ?? 0) > 0}
      <span>
        {status.syncCompletedDataTypes} of {status.syncTotalDataTypes} data types read
      </span>
    {/if}
    {#if status.syncPagesRead === 1}
      <span>1 Google page read for this data type</span>
    {:else if (status.syncPagesRead ?? 0) > 1}
      <span>{status.syncPagesRead} Google pages read for this data type</span>
    {/if}
    {#if status.lastSync}
      <span>Last successful sync: {lastSeen(status.lastSync)}</span>
    {/if}
  </div>
  <p class="text-sm">
    You can keep using Nocturne or leave this page. The server will continue the
    import.
  </p>
</div>
