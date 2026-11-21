<script lang="ts">
  import { TenantReviewUnavailable } from "@nocturne/api-client";
  import Lock from "@lucide/svelte/icons/lock";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import type { ReviewCell } from "./review-lens.js";

  type Notice = Exclude<ReviewCell, { kind: "figures" }>;

  interface Props {
    cell: Notice;
    class?: string;
  }

  const { cell, class: className = "" }: Props = $props();

  const failed = $derived(
    cell.kind === "missing" ||
      (cell.kind === "unavailable" &&
        cell.reason === TenantReviewUnavailable.Failed)
  );
  const lockedBy = $derived(
    cell.kind === "unavailable" && !failed ? cell.reason : null
  );
</script>

{#if cell.kind === "loading"}
  <div class={className} data-testid="review-loading">
    <Skeleton class="h-4 w-40" />
    <span class="sr-only">Loading statistics</span>
  </div>
{:else if lockedBy}
  <p
    class="flex items-center gap-1.5 text-sm text-muted-foreground {className}"
    data-testid="review-notice"
  >
    <Lock class="size-3.5 shrink-0" aria-hidden="true" />
    {#if lockedBy === TenantReviewUnavailable.HistoryLimited}
      24 hours only. Statistics for a period are not available.
    {:else}
      You do not have access to this person's reports.
    {/if}
  </p>
{:else if failed}
  <p class="text-sm text-muted-foreground {className}" data-testid="review-notice">
    The statistics did not load.
  </p>
{:else if cell.kind === "empty"}
  <p class="text-sm text-muted-foreground {className}" data-testid="review-notice">
    No readings in this period.
  </p>
{/if}
