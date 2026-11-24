<script lang="ts">
  import { formatNumber } from "#lib/utils/formatting.js";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import * as migrationRemote from "#lib/api/generated/migrations.generated.remote.js";
  import { MigrationJobState, MigrationPhase } from "#lib/api/index.js";
  import type { CollectionProgress } from "#lib/api/index.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { findSessionJob } from "../migration-session";
  import CollectionLanes from "#lib/components/migration/CollectionLanes.svelte";
  import TimeRemaining from "#lib/components/migration/TimeRemaining.svelte";
  import { Progress } from "#lib/components/ui/progress/index.js";
  import type { ImportResult } from "./Finish.svelte";
  import type { PatientVoice } from "#lib/onboarding/patient-voice.svelte.js";
  import { fresh } from "#lib/api/fresh-query.js";

  let {
    jobId,
    startError,
    voice,
    onProgressChange,
    onResult,
    onSettled,
    onRecentReady,
    onComplete,
  }: {
    jobId?: string;
    /** Why the run could not be started, when that was attempted and failed. */
    startError?: string;
    voice: PatientVoice;
    onProgressChange?: (pct: number) => void;
    /** Fires once the server reports the run ended, including a run that ended before this mounted. */
    onResult?: (result: Exclude<ImportResult, null>) => void;
    /** Fires once there is nothing left to wait for here: the run ended, none exists, or status polling was lost. */
    onSettled?: () => void;
    /**
     * Fires once a live run has its recent data in and only older history left. Watching ends
     * here: the app follows the rest of the run.
     */
    onRecentReady?: (jobId: string) => void;
    onComplete: () => void;
  } = $props();

  let progress = $state(0); // displayed progress (smoothed)
  let realProgress = $state(0); // actual backend progress
  let error = $state<string | null>(null);
  let startReason = $state<string | null>(null);
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
  let collections = $state<Record<string, CollectionProgress>>({});
  let currentCollection = $state<string | undefined>(undefined);
  let phase = $state<MigrationPhase | undefined>(undefined);
  let remaining = $state<string | undefined>(undefined);
  let totalMigrated = $state(0);

  const MIN_DURATION_MS = 3000; // minimum time the progress animation takes
  let startedAt = $state(0);

  const hasCollections = $derived(Object.keys(collections).length > 0);
  const nothingToShow = $derived(error !== null && !hasCollections);
  const faulted = $derived(failed || (done && caveatIsFault));

  $effect(() => {
    let active = true;

    async function poll() {
      let resolvedJobId = jobId;

      if (!resolvedJobId) {
        try {
          resolvedJobId = findSessionJob(
            await fresh(migrationRemote.getHistory())
          );
        } catch (err) {
          error = remoteErrorMessage(err, "Could not look for the import.");
          loading = false;
          onSettled?.();
          return;
        }
      }

      // This component only MONITORS an existing migration — it never starts one.
      // The import is kicked off explicitly when the user connects their source
      // (see the setup page's handleMigrationConnected), so a job should already
      // exist by now. If none is found there is nothing to monitor.
      if (!resolvedJobId) {
        error = startError
          ? "We could not start the import from Nightscout. You can continue and run it later from Settings."
          : "No import from Nightscout is running. You can continue and start one later from Settings.";
        startReason = startError ?? null;
        loading = false;
        onSettled?.();
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
          const status = await fresh(migrationRemote.getStatus(resolvedJobId));
          if (!active) break;
          consecutiveFailures = 0; // a successful poll clears the transient-failure streak

          realProgress = status.progressPercentage ?? 0;
          phase = status.phase;
          currentCollection = status.currentCollection;
          remaining = status.estimatedTimeRemaining;
          collections = status.collectionProgress ?? {};
          totalMigrated = Object.values(collections).reduce(
            (sum, c) => sum + (c.documentsMigrated ?? 0),
            0
          );

          // Check terminal states
          if (status.state === MigrationJobState.Completed) {
            // Already done on the very first poll — user navigated back to review
            if (firstPoll) wasAlreadyDone = true;
            done = true;
            caveat = status.errorMessage ?? null;
            caveatIsFault = status.hasFailures ?? false;
            realProgress = 100;
            onResult?.(caveatIsFault ? "partial" : "complete");
            onSettled?.();
            break;
          }
          if (
            status.state === MigrationJobState.Failed ||
            status.state === MigrationJobState.Cancelled ||
            status.state === MigrationJobState.Interrupted
          ) {
            failed = true;
            error =
              status.state === MigrationJobState.Cancelled
                ? "The import was canceled."
                : status.state === MigrationJobState.Interrupted
                  ? "The import stopped because the server restarted. Start it again from Settings."
                  : (status.errorMessage ?? "The import stopped because of an error.");
            onResult?.("failed");
            onSettled?.();
            break;
          }

          // Start smoothing timer after first successful poll of a live migration
          if (firstPoll) {
            firstPoll = false;
            startedAt = Date.now();
            onResult?.("running");
          }

          if (status.recentDataReady && onRecentReady) {
            onResult?.("background");
            onSettled?.();
            onRecentReady(resolvedJobId);
            break;
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
            error =
              "We lost track of the import's progress. It may still be running. Check Settings to see how it ended.";
            onResult?.("running");
            onSettled?.();
            break;
          }
          await new Promise((resolve) => setTimeout(resolve, 2000));
        }
      }
    }

    // Deferred so this effect does not track what the load reads.
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
      if (failed || error) {
        clearInterval(interval);
        return;
      }

      const elapsed = Date.now() - startedAt;
      const timeCap = Math.min(100, (elapsed / MIN_DURATION_MS) * 100);
      const smoothed = Math.min(realProgress, timeCap);

      if (smoothed > progress) {
        progress = smoothed;
        onProgressChange?.(progress);
      }

      // Pause at 100% before advancing to next step. A run that left something out stays, so
      // its summary can be read; the wizard offers Continue.
      if (done && progress >= 100) {
        if (caveatIsFault) {
          clearInterval(interval);
        } else if (completedAt === 0) {
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

<div class="@container flex flex-col gap-8 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-brand font-hairline leading-tight tracking-tight text-foreground text-3xl @lg:text-4xl @min-[40rem]:text-5xl"
    >
      {#if voice.kind === "self"}
        Bringing your <em class="not-italic font-light text-primary">history</em> across.
      {:else if voice.kind === "named"}
        Bringing {voice.name}'s <em class="not-italic font-light text-primary">history</em> across.
      {:else}
        Bringing the <em class="not-italic font-light text-primary">history</em> across.
      {/if}
    </h1>
    {#if !nothingToShow}
      <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
        Nocturne is copying entries, treatments, and profiles from Nightscout.
        The import continues on the server if you close this page.
      </p>
    {/if}
  </div>

  {#if loading}
    <div class="flex flex-col items-center justify-center py-16 gap-4">
      <Loader2
        class="h-12 w-12 animate-spin text-primary"
      />
      <p class="text-sm text-muted-foreground">Looking for the import...</p>
    </div>
  {:else if nothingToShow}
    <div class="flex flex-col items-center justify-center py-16 gap-4">
      <AlertTriangle class="h-12 w-12 text-warning" />
      <p class="text-sm text-muted-foreground">{error}</p>
      {#if startReason}
        <p class="text-xs text-muted-foreground">{startReason}</p>
      {/if}
    </div>
  {:else}
    <div class="flex flex-col gap-3">
      <span
        class="font-brand font-hairline tabular-nums text-5xl @lg:text-6xl {faulted
          ? 'text-warning'
          : 'text-primary'}"
      >
        {Math.round(progress)}%
      </span>
      <Progress
        value={progress}
        tone={faulted ? "warning" : "default"}
        aria-label="Import progress"
        data-testid="import-progress"
      />
      <p class="text-sm text-muted-foreground">
        {#if error}
          <span class="text-warning">{error}</span>
        {:else if caveat}
          <span class={caveatIsFault ? "text-warning" : ""}>{caveat}</span>
        {:else if phase === MigrationPhase.Connecting}
          Connecting to Nightscout
        {:else if phase === MigrationPhase.Counting}
          Counting the records in Nightscout
        {:else if remaining && !done}
          <TimeRemaining {remaining} /> &middot; {formatNumber(totalMigrated)} records
        {:else}
          {formatNumber(totalMigrated)} records imported
        {/if}
      </p>
    </div>

    {#if hasCollections}
      <CollectionLanes {collections} {currentCollection} />
    {/if}
  {/if}
</div>

