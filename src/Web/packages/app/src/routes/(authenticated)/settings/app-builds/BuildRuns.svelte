<script lang="ts">
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import { AppBuildRunState } from "#lib/api/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { getAppBuildRuns } from "#lib/api/generated/appBuilds.generated.remote.js";
  import { formatDateTime } from "#lib/utils/formatting.js";
  import { httpsHref } from "#lib/utils/url.js";
  import { describeError, runStateLabel } from "./app-builds-copy.svelte";
  import RunsPoller from "./RunsPoller.svelte";

  interface Props {
    targetId: string;
    /**
     * Poll even with no run in progress: a build was just started and its run
     * is not listed yet.
     */
    expectingRun?: boolean;
    pollMs?: number;
  }

  let { targetId, expectingRun = false, pollMs = 10_000 }: Props = $props();

  const runsQuery = $derived(getAppBuildRuns(targetId));
  const runs = $derived(runsQuery.current ?? []);
  const inProgress = $derived(
    runs.some(
      (run) =>
        run.state === AppBuildRunState.Queued ||
        run.state === AppBuildRunState.Running
    )
  );
</script>

{#if inProgress || expectingRun}
  <RunsPoller refresh={() => void runsQuery.refresh()} intervalMs={pollMs} />
{/if}

<div class="space-y-3">
  <h3 class="font-medium">Recent builds</h3>
  <p class="text-sm text-muted-foreground">
    When a build succeeds, download the APK file from the build page on GitHub.
    GitHub keeps the file for 14 days.
  </p>
  {#if runsQuery.error}
    <p role="alert" class="text-sm text-destructive">
      {describeError(runsQuery.error)}
    </p>
  {:else if runs.length === 0}
    <p class="text-sm text-muted-foreground">No builds yet.</p>
  {:else}
    <ul class="divide-y rounded-lg border" aria-label="Recent builds">
      {#each runs as run (run.id)}
        {@const href = httpsHref(run.htmlUrl)}
        <li class="flex flex-wrap items-center justify-between gap-2 p-3">
          <div class="min-w-0 space-y-1">
            <p class="truncate text-sm font-medium">
              {run.title ?? `#${run.runNumber}`}
            </p>
            <p class="text-xs text-muted-foreground">
              {formatDateTime(run.createdAt)}
            </p>
          </div>
          <div class="flex items-center gap-3">
            <Badge variant="secondary">{runStateLabel(run.state)}</Badge>
            {#if href}
              <!-- eslint-disable svelte/no-navigation-without-resolve -- a GitHub page, not an app route -->
              <a
                class="inline-flex items-center gap-1 text-sm underline"
                {href}
                target="_blank"
                rel="noreferrer"
              >
                <ExternalLink class="h-3.5 w-3.5" />
                Open on GitHub
              </a>
              <!-- eslint-enable svelte/no-navigation-without-resolve -->
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</div>
