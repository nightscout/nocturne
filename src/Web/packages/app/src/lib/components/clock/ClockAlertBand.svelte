<script lang="ts">
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import Check from "@lucide/svelte/icons/check";
  import Eye from "@lucide/svelte/icons/eye";
  import { Button } from "#lib/components/ui/button/index.js";
  import type { ClockAlertDto } from "#lib/api/index.js";
  import { severity } from "#lib/components/alerts/severity.js";
  import { severityLabel } from "#lib/components/alerts/severityLabel.svelte.js";
  import { formatDateTimeCompact, relativeAge, toDate } from "#lib/utils/formatting.js";

  interface Props {
    alerts: ClockAlertDto[];
    /** Reactive clock so the relative time ages while the band is on screen. */
    now: number;
    /** Records that someone here saw the alarm; absent when the face's owner did not allow it. */
    onSeen?: (excursionId: string) => Promise<void>;
  }

  let { alerts, now, onSeen }: Props = $props();

  let recording = $state<string | null>(null);

  async function seen(id: string) {
    if (!onSeen) return;
    recording = id;
    try {
      await onSeen(id);
    } finally {
      recording = null;
    }
  }
</script>

{#if alerts.length > 0}
  <div class="fixed inset-x-0 top-0 z-40 space-y-1 p-3" role="alert">
    {#each alerts as alert (alert.id)}
      <div
        class="mx-auto flex max-w-3xl items-center gap-3 rounded-lg border bg-black/80 px-4 py-3 {severity(
          alert.severity,
          'strip'
        )}"
      >
        <AlertTriangle class="size-6 shrink-0" />
        <div class="min-w-0 flex-1">
          <div class="text-xs font-semibold uppercase tracking-wider">
            {severityLabel(alert.severity)}
          </div>
          <div class="truncate text-xl font-medium">
            {alert.ruleName || "Alert"}
          </div>
          <div
            class="text-sm text-white/70"
            title={toDate(alert.startedAt) ? formatDateTimeCompact(alert.startedAt) : undefined}
          >
            {relativeAge(alert.startedAt, { now, fallback: "Unknown" })}
          </div>
        </div>
        {#if onSeen && !alert.seenHere}
          <Button
            variant="overlay"
            size="sm"
            disabled={recording === alert.id}
            onclick={() => seen(alert.id ?? "")}
          >
            <Eye class="size-4" />
            Seen
          </Button>
        {:else if alert.seenHere}
          <span class="inline-flex items-center gap-1 text-sm text-white/70">
            <Eye class="size-4" />
            Seen here
          </span>
        {:else if alert.acknowledged}
          <span class="inline-flex items-center gap-1 text-sm text-white/70">
            <Check class="size-4" />
            Acknowledged
          </span>
        {:else if alert.snoozedUntil}
          <span class="inline-flex items-center gap-1 text-sm text-white/70">
            <BellOff class="size-4" />
            Snoozed
          </span>
        {/if}
      </div>
    {/each}
  </div>
{/if}
