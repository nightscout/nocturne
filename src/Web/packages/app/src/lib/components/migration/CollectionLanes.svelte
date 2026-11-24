<script lang="ts">
  import { formatNumber } from "#lib/utils/formatting.js";
  import { SkippedRecordsNote } from "#lib/components/shared/index.js";
  import type { CollectionProgress } from "#lib/api/index.js";
  import Database from "@lucide/svelte/icons/database";
  import Upload from "@lucide/svelte/icons/upload";
  import Users from "@lucide/svelte/icons/users";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import BookOpen from "@lucide/svelte/icons/book-open";
  import Activity from "@lucide/svelte/icons/activity";
  import KeyRound from "@lucide/svelte/icons/key-round";
  import Check from "@lucide/svelte/icons/check";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Loader2 from "@lucide/svelte/icons/loader-circle";

  let {
    collections,
    currentCollection,
  }: {
    collections: Record<string, CollectionProgress>;
    /** The key of the collection the run is pulling now, when it is pulling one. */
    currentCollection?: string;
  } = $props();

  // A MongoDB import can carry a collection with any name. One with no entry here is shown
  // by its own name.
  const COLLECTION_META: Record<string, { icon: typeof Database; label: string }> = {
    subjects: { icon: KeyRound, label: "Access tokens" },
    entries: { icon: Database, label: "Entries" },
    treatments: { icon: Upload, label: "Treatments" },
    profile: { icon: Users, label: "Profiles" },
    devicestatus: { icon: ChartLine, label: "Device statuses" },
    food: { icon: BookOpen, label: "Food library" },
    activity: { icon: Activity, label: "Activities" },
  };

  const lanes = $derived(
    Object.entries(collections).map(([key, col]) => {
      const meta = COLLECTION_META[key] ?? { icon: Database, label: key };
      const isComplete = col.isComplete ?? false;
      const total = col.totalDocuments ?? 0;
      return {
        key,
        icon: meta.icon,
        label: meta.label,
        total,
        migrated: col.documentsMigrated ?? 0,
        failed: col.documentsFailed ?? 0,
        pct: Math.round(col.progressPercentage ?? 0),
        failureReason: col.failureReason ?? null,
        skippedReason: col.skippedReason ?? null,
        isDone: isComplete && !col.failureReason && !col.skippedReason,
        isActive: !isComplete && key === currentCollection,
        // The source gave no count, so there is no share to show until the pull ends.
        isUncounted: !isComplete && total === 0,
        skippedDeleted: col.recordsSkippedDeleted ?? 0,
        skippedUnsupported: col.documentsSkippedUnsupported ?? 0,
      };
    })
  );
</script>

<ul class="flex flex-col divide-y">
  {#each lanes as lane (lane.key)}
    {@const touched = lane.isDone || lane.isActive || lane.migrated > 0}
    <li
      class="flex flex-col gap-2 py-3"
      data-testid="collection-lane-{lane.key}"
    >
      <div class="grid grid-cols-[auto_1fr_auto] gap-3 items-center">
        <lane.icon
          class="size-4 {touched ? 'text-primary' : 'text-muted-foreground'}"
          aria-hidden="true"
        />

        <div class="flex flex-col">
          <span class="text-sm font-medium">{lane.label}</span>
          <span class="font-mono text-xs text-muted-foreground">
            {#if lane.skippedReason}
              not imported
            {:else if lane.total > 0}
              {formatNumber(lane.migrated)} / {formatNumber(lane.total)} records
            {:else if lane.migrated > 0}
              {formatNumber(lane.migrated)} records
            {:else if lane.failureReason}
              not imported
            {:else if lane.isDone}
              no records
            {:else if lane.isActive}
              starting
            {:else}
              pending
            {/if}
          </span>
        </div>

        <div class="flex items-center justify-end">
          {#if lane.failureReason}
            <AlertTriangle class="h-4 w-4 text-warning" aria-label="Failed" />
          {:else if lane.skippedReason}
            <span class="font-mono text-xs text-muted-foreground">skipped</span>
          {:else if lane.isDone}
            <Check class="h-4 w-4 text-primary" aria-label="Complete" />
          {:else if lane.isUncounted}
            {#if lane.isActive}
              <Loader2 class="h-4 w-4 animate-spin text-muted-foreground" aria-label="In progress" />
            {/if}
          {:else}
            <span class="font-mono text-xs text-muted-foreground">{lane.pct}%</span>
          {/if}
        </div>
      </div>

      {#if lane.failureReason}
        <p class="text-xs text-warning">{lane.failureReason}</p>
      {:else if lane.skippedReason}
        <p class="text-xs text-muted-foreground">{lane.skippedReason}</p>
      {/if}

      {#if lane.failed > 0}
        <p class="text-xs text-warning">
          {#if lane.failed === 1}
            1 record could not be imported.
          {:else}
            {formatNumber(lane.failed)} records could not be imported.
          {/if}
        </p>
      {/if}

      <SkippedRecordsNote
        deleted={lane.skippedDeleted}
        unsupported={lane.skippedUnsupported}
        class="text-xs text-muted-foreground"
      />

      {#if !lane.skippedReason}
        <div class="h-1 w-full overflow-hidden rounded-full bg-muted">
          {#if lane.isUncounted}
            {#if lane.isActive}
              <div class="h-full w-full rounded-full bg-primary/40 animate-pulse"></div>
            {/if}
          {:else}
            <div
              class="h-full w-(--progress) rounded-full transition-all duration-500 ease-out {lane.failureReason
                ? 'bg-warning'
                : lane.isDone
                  ? 'bg-success'
                  : 'bg-primary'}"
              style:--progress="{lane.isDone ? 100 : lane.pct}%"
            ></div>
          {/if}
        </div>
      {/if}
    </li>
  {/each}
</ul>
