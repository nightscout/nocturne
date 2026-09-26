<script lang="ts">
  import { formatNumber } from "$lib/utils/formatting";
  import {
    Database,
    Upload,
    Users,
    ChartLine,
    BookOpen,
    Check,
    AlertTriangle,
    Loader2,
  } from "lucide-svelte";
  import * as migrationRemote from "$api/generated/migrations.generated.remote";
  import { MigrationJobState } from "$api";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { findSessionJob } from "../migration-session";
  import { SkippedRecordsNote } from "$lib/components/shared";
  import type { ImportResult } from "./Finish.svelte";

  let {
    jobId,
    onProgressChange,
    onResult,
    onComplete,
  }: {
    jobId?: string;
    onProgressChange?: (pct: number) => void;
    /** Fires once the server reports the run ended, including a run that ended before this mounted. */
    onResult?: (result: Exclude<ImportResult, null>) => void;
    onComplete: () => void;
  } = $props();

  let progress = $state(0); // displayed progress (smoothed)
  let realProgress = $state(0); // actual backend progress
  let error = $state<string | null>(null);
  let failed = $state(false);
  // A run can complete having imported only part of what was asked for; the server says which
  // collections did not arrive. Without this the wizard shows that run as an unqualified success.
  let caveat = $state<string | null>(null);
  // Whether that summary reports a fault. A collection can also be skipped — a read-only API
  // secret cannot list sign-ins, which is how most sites are set up — and colouring that as a
  // warning would put one on every healthy import.
  let caveatIsFault = $state(false);
  let loading = $state(true);
  let done = $state(false); // backend says completed
  let wasAlreadyDone = $state(false); // migration was already complete when we mounted
  let collections = $state<
    {
      key: string;
      icon: typeof Database;
      label: string;
      total: number;
      migrated: number;
      pct: number;
      isComplete: boolean;
      skippedDeleted: number;
      skippedUnsupported: number;
    }[]
  >([]);
  let etaText = $state<string | null>(null);
  let totalMigrated = $state(0);
  let currentOperation = $state<string | null>(null);

  const circumference = 2 * Math.PI * 88; // ≈ 553.0
  const MIN_DURATION_MS = 3000; // minimum time the progress animation takes
  let startedAt = $state(0);

  const COLLECTION_META: Record<
    string,
    { icon: typeof Database; label: string }
  > = {
    entries: { icon: Database, label: "Entries" },
    treatments: { icon: Upload, label: "Treatments" },
    profile: { icon: Users, label: "Profiles" },
    devicestatus: { icon: ChartLine, label: "Device statuses" },
    food: { icon: BookOpen, label: "Food library" },
    activity: { icon: ChartLine, label: "Activities" },
  };

  function formatTimeSpan(ts: string | undefined): string | null {
    if (!ts) return null;
    // .NET TimeSpan format: "HH:MM:SS.fff" or "D.HH:MM:SS.fff"
    const dotSplit = ts.split(".");
    let timePart = dotSplit.length > 2 ? dotSplit[1] : dotSplit[0];
    const parts = timePart.split(":");
    if (parts.length < 2) return ts;
    const hours = parseInt(parts[0] ?? "0");
    const minutes = parseInt(parts[1] ?? "0");
    const seconds = parseInt(parts[2]?.split(".")[0] ?? "0");
    if (hours > 0) return `${hours}h ${minutes}m`;
    if (minutes > 0) return `${minutes} min ${seconds} sec`;
    return `${seconds} sec`;
  }

  function formatCount(n: number): string {
    return formatNumber(n);
  }

  $effect(() => {
    let active = true;

    async function poll() {
      let resolvedJobId = jobId;

      if (!resolvedJobId) {
        try {
          resolvedJobId = findSessionJob(
            await migrationRemote.getHistory().run()
          );
        } catch (err) {
          error = remoteErrorMessage(err, "Failed to find active migration");
          loading = false;
          return;
        }
      }

      // This component only MONITORS an existing migration — it never starts one.
      // The import is kicked off explicitly when the user connects their source
      // (see the setup page's handleMigrationConnected), so a job should already
      // exist by now. If none is found there is nothing to monitor.
      if (!resolvedJobId) {
        loading = false;
        return;
      }

      loading = false;
      let firstPoll = true;
      let consecutiveFailures = 0;
      // The migration runs server-side on a detached background task — it continues even if
      // this screen is closed. A single failed status poll (a transient network blip, or the
      // API being briefly busy importing a large dataset) must therefore NOT abort the UI.
      // Tolerate a few consecutive failures before surfacing an error.
      const MAX_CONSECUTIVE_FAILURES = 5;

      // Poll loop — update real data from backend
      // startedAt is set after the first successful poll of a live (non-complete) migration
      while (active) {
        try {
          const status = await migrationRemote.getStatus(resolvedJobId).run();
          if (!active) break;
          consecutiveFailures = 0; // a successful poll clears the transient-failure streak

          realProgress = status.progressPercentage ?? 0;
          currentOperation = status.currentOperation ?? null;
          etaText = formatTimeSpan(status.estimatedTimeRemaining);

          // Build collection lanes from real data
          const cp = status.collectionProgress ?? {};
          collections = Object.entries(cp).map(([key, col]) => {
            const meta = COLLECTION_META[key] ?? { icon: Database, label: key };
            const total = col.totalDocuments ?? 0;
            const migrated = col.documentsMigrated ?? 0;
            const pct = Math.round(col.progressPercentage ?? 0);
            return {
              key,
              icon: meta.icon,
              label: meta.label,
              total,
              migrated,
              pct,
              isComplete: col.isComplete ?? false,
              skippedDeleted: col.recordsSkippedDeleted ?? 0,
              skippedUnsupported: col.documentsSkippedUnsupported ?? 0,
            };
          });

          totalMigrated = collections.reduce((sum, c) => sum + c.migrated, 0);

          // Check terminal states
          if (status.state === MigrationJobState.Completed) {
            // Already done on the very first poll — user navigated back to review
            if (firstPoll) wasAlreadyDone = true;
            done = true;
            caveat = status.errorMessage ?? null;
            caveatIsFault = Object.values(cp).some((col) => col.failureReason);
            realProgress = 100;
            onResult?.(caveatIsFault ? "partial" : "complete");
            break;
          }
          if (
            status.state === MigrationJobState.Failed ||
            status.state === MigrationJobState.Cancelled ||
            status.state === MigrationJobState.Interrupted
          ) {
            failed = true;
            error =
              status.errorMessage ?? `Migration ${String(status.state).toLowerCase()}`;
            onResult?.("failed");
            break;
          }

          // Start smoothing timer after first successful poll of a live migration
          if (firstPoll) {
            firstPoll = false;
            startedAt = Date.now();
          }

          await new Promise((resolve) => setTimeout(resolve, 2000));
        } catch {
          // A single failed poll says nothing worth showing; the retry below is
          // the response, and a run of them gets its own wording.
          if (!active) break;
          consecutiveFailures++;
          // Only give up — and tell the user — once polling has failed repeatedly. The import
          // itself keeps running on the server regardless of what we show here.
          if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
            error = "Lost connection to migration status";
            break;
          }
          await new Promise((resolve) => setTimeout(resolve, 2000));
        }
      }
    }

    // `.run()` rejects during the render/effect flush, so defer out of it.
    queueMicrotask(() => {
      if (active) poll();
    });
    return () => {
      active = false;
    };
  });

  // Smooth displayed progress: never jumps ahead of real, but enforces a
  // minimum animation duration so fast imports don't flash past instantly.
  // If the job is already complete (e.g. re-visiting a finished migration),
  // skip smoothing entirely.
  $effect(() => {
    // Already done on first render (navigated back) — show 100%, don't auto-advance
    if (wasAlreadyDone) {
      progress = 100;
      onProgressChange?.(100);
      return;
    }

    if (startedAt === 0) return;

    let completedAt = 0;

    const interval = setInterval(() => {
      const elapsed = Date.now() - startedAt;
      const timeCap = Math.min(100, (elapsed / MIN_DURATION_MS) * 100);
      const smoothed = Math.min(realProgress, timeCap);

      if (smoothed > progress) {
        progress = smoothed;
        onProgressChange?.(progress);
      }

      // Pause at 100% before advancing to next step
      if (done && progress >= 100) {
        if (completedAt === 0) {
          completedAt = Date.now();
        } else if (Date.now() - completedAt >= 1500) {
          clearInterval(interval);
          onComplete();
        }
      }
    }, 50);

    return () => clearInterval(interval);
  });
</script>

<div class="flex flex-col gap-8 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl md:text-4xl xl:text-5xl"
    >
      Bringing your <em
        class="not-italic font-light text-primary"
      >
        history
      </em>
      across.
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
      We're streaming entries, treatments, and profiles from your Nightscout
      into Nocturne's store. You can navigate away &mdash; this continues in the
      background.
    </p>
  </div>

  {#if loading}
    <div class="flex flex-col items-center justify-center py-16 gap-4">
      <Loader2
        class="h-12 w-12 animate-spin text-primary"
      />
      <p class="text-sm text-muted-foreground">Finding active migration...</p>
    </div>
  {:else if error && !collections.length}
    <div class="flex flex-col items-center justify-center py-16 gap-4">
      <AlertTriangle class="h-12 w-12 text-warning" />
      <p class="text-sm text-muted-foreground">{error}</p>
    </div>
  {:else}
    <!-- Import hero card -->
    <div
      class="grid grid-cols-[1fr_260px] max-sm:grid-cols-1 max-sm:justify-items-center gap-6 p-7 rounded-2xl border overflow-hidden relative bg-card"
    >
      <!-- Left side -->
      <div class="flex flex-col gap-3">
        <span
          class="font-mono text-xs uppercase tracking-widest text-muted-foreground"
        >
          Overall progress
        </span>
        <div class="flex items-baseline gap-1">
          <span
            class="font-brand font-hairline tabular-nums text-5xl md:text-6xl xl:text-7xl text-primary"
          >
            {Math.round(progress)}
          </span>
          <span
            class="text-3xl font-brand font-hairline tabular-nums text-muted-foreground"
          >
            %
          </span>
        </div>
        <p class="text-sm text-muted-foreground">
          {#if failed}
            <span class="text-warning">{error}</span>
          {:else if caveat}
            <span class={caveatIsFault ? "text-warning" : ""}>{caveat}</span>
          {:else if etaText}
            About <span class="font-semibold text-muted-foreground">{etaText}</span>
            remaining &middot; {formatCount(totalMigrated)} records
          {:else}
            {formatCount(totalMigrated)} records migrated
          {/if}
        </p>
        {#if currentOperation}
          <p class="font-mono text-xs text-muted-foreground">{currentOperation}</p>
        {/if}
      </div>

      <!-- Right side — Progress ring -->
      <div
        class="relative flex items-center justify-center w-[200px] h-[200px]"
      >
        <svg
          width="200"
          height="200"
          viewBox="0 0 200 200"
          class="-rotate-90"
        >
          <!-- Track -->
          <circle
            cx="100"
            cy="100"
            r="88"
            fill="none"
            class="stroke-border"
            stroke-width="6"
          />
          <!-- Progress arc -->
          <circle
            cx="100"
            cy="100"
            r="88"
            fill="none"
            stroke="currentColor"
            class="progress-arc {failed ? 'text-warning' : 'text-primary'}"
            stroke-width="6"
            stroke-linecap="round"
            stroke-dasharray={circumference}
            stroke-dashoffset={circumference * (1 - progress / 100)}
          />
        </svg>
        <!-- Center overlay -->
        <span
          class="absolute font-brand text-4xl font-light tabular-nums text-foreground"
        >
          {Math.round(progress)}%
        </span>
      </div>
    </div>

    <!-- Import lanes -->
    {#if collections.length > 0}
      <div class="flex flex-col gap-2.5">
        {#each collections as col (col.key)}
          <div
            class="flex flex-col gap-2 p-3.5 px-4 rounded-xl border bg-card"
          >
            <div class="grid grid-cols-[36px_1fr_auto] gap-3 items-center">
              <!-- Icon box -->
              <div
                class="flex h-9 w-9 items-center justify-center rounded-lg {col.isComplete ||
                col.pct > 0
                  ? 'bg-primary/10'
                  : 'bg-muted'}"
              >
                <col.icon
                  class="h-[18px] w-[18px] {col.isComplete || col.pct > 0
                    ? 'text-primary'
                    : 'text-muted-foreground'}"
                />
              </div>

              <!-- Name & meta -->
              <div class="flex flex-col">
                <span class="text-sm font-medium">{col.label}</span>
                <span class="font-mono text-xs text-muted-foreground">
                  {#if col.total > 0}
                    {formatCount(col.migrated)} / {formatCount(col.total)} records
                  {:else if col.migrated > 0}
                    {formatCount(col.migrated)} records
                  {:else if col.isComplete}
                    no records
                  {:else}
                    pending
                  {/if}
                </span>
              </div>

              <!-- Percentage or checkmark -->
              <div class="flex items-center justify-end">
                {#if col.isComplete}
                  <Check class="h-4 w-4 text-primary" />
                {:else}
                  <span class="font-mono text-xs text-muted-foreground">
                    {col.pct}%
                  </span>
                {/if}
              </div>
            </div>

            <SkippedRecordsNote
              deleted={col.skippedDeleted}
              unsupported={col.skippedUnsupported}
              class="text-xs text-muted-foreground"
            />

            <!-- Progress bar -->
            <div
              class="h-1 w-full overflow-hidden rounded-full bg-muted"
            >
              <div
                class="h-full w-(--progress) rounded-full transition-all duration-500 ease-out {col.isComplete
                  ? 'bg-success'
                  : 'bg-primary'}"
                style:--progress="{col.isComplete ? 100 : col.pct}%"
              ></div>
            </div>
          </div>
        {/each}
      </div>
    {/if}
  {/if}
</div>

<style>
  .progress-arc {
    transition: stroke-dashoffset 0.6s ease;
    filter: drop-shadow(0 0 6px currentColor);
  }
</style>
