<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import {
    getActiveAlerts,
    snoozeInstance,
    acknowledge,
  } from "$api/generated/alerts.generated.remote";
  import { toggleRule } from "$api/generated/alertRules.generated.remote";
  import type { ActiveExcursionResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { Bell, BellOff, X, Loader2 } from "lucide-svelte";
  import { severity } from "./severity";

  /**
   * App-wide fresh-fire toast. Polls the active-alerts surface; whenever a new
   * alert id appears (i.e. one we haven't shown before this session), surface a
   * top-center toast with Snooze / Dismiss / Mute-rule actions.
   *
   * The component intentionally does _not_ show every active alert — that's the
   * persistent banner's job (currently <see cref="AlertBanner"/>). This is for
   * the trust-critical "you should know about this RIGHT NOW" moment.
   */

  // Polling cadence — kept aligned with AlertBanner so we don't double-poll.
  const POLL_MS = 10_000;

  let alerts = $state<ActiveExcursionResponse[]>([]);
  // Toasts are appended whenever a new alert id appears; users dismiss them
  // explicitly. We don't auto-dismiss so the trust gesture is intentional.
  let queue = $state<ActiveExcursionResponse[]>([]);
  // Tracks which ids we've already shown so a re-poll doesn't spawn dupes.
  let seen = $state<Set<string>>(new Set());
  let busyForId = $state<string | null>(null);
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  async function poll(): Promise<void> {
    try {
      const result = await getActiveAlerts();
      const list = Array.isArray(result) ? result : [];
      alerts = list;
      const fresh: ActiveExcursionResponse[] = [];
      for (const a of list) {
        const id = a.id ?? "";
        if (!id || seen.has(id)) continue;
        seen.add(id);
        fresh.push(a);
      }
      if (fresh.length > 0) queue = [...fresh, ...queue];
    } catch {
      // Silent: polling shouldn't surface transient errors.
    }
  }

  onMount(() => {
    poll();
    pollTimer = setInterval(poll, POLL_MS);
  });

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
  });

  function dismiss(id: string): void {
    queue = queue.filter((a) => a.id !== id);
  }

  async function snooze(id: string, minutes: number): Promise<void> {
    busyForId = id;
    try {
      await snoozeInstance({ instanceId: id, request: { minutes } });
      dismiss(id);
    } finally {
      busyForId = null;
    }
  }

  async function ack(id: string): Promise<void> {
    busyForId = id;
    try {
      await acknowledge({ acknowledgedBy: "web_user" });
      dismiss(id);
    } finally {
      busyForId = null;
    }
  }

  async function muteRule(
    id: string,
    ruleId: string | undefined
  ): Promise<void> {
    if (!ruleId) {
      dismiss(id);
      return;
    }
    busyForId = id;
    try {
      await toggleRule(ruleId);
      dismiss(id);
    } finally {
      busyForId = null;
    }
  }
</script>

{#if queue.length > 0}
  <div
    role="region"
    aria-label="Fresh alerts"
    class="pointer-events-none fixed inset-x-0 top-4 z-50 flex flex-col items-center gap-2 px-4"
  >
    {#each queue as a (a.id)}
      <div
        class="pointer-events-auto w-full max-w-md rounded-lg border bg-card p-3 shadow-lg ring-1 ring-black/5"
        role="alert"
      >
        <div class="flex items-start gap-2">
          <span
            class="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-full {severity('critical', 'chip')}"
          >
            <Bell class="h-4 w-4" />
          </span>
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <span class="text-sm font-semibold truncate">
                {a.ruleName ?? "Alert"}
              </span>
              <span
                class="ml-auto text-[10px] uppercase tracking-wider text-muted-foreground"
              >
                just now
              </span>
            </div>
            {#if a.subjectName}
              <div class="text-xs text-muted-foreground truncate">
                {a.subjectName}
              </div>
            {/if}
            <div class="mt-2 flex flex-wrap items-center gap-1">
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 5)}
                disabled={busyForId === a.id}
              >
                5m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 15)}
                disabled={busyForId === a.id}
              >
                15m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 30)}
                disabled={busyForId === a.id}
              >
                30m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 60)}
                disabled={busyForId === a.id}
              >
                1h
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                class="h-7 px-2 text-xs ml-auto"
                onclick={() => ack(a.id ?? "")}
                disabled={busyForId === a.id}
                title="Acknowledge"
              >
                {#if busyForId === a.id}
                  <Loader2 class="h-3.5 w-3.5 animate-spin" />
                {:else}
                  Dismiss
                {/if}
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => muteRule(a.id ?? "", a.ruleId)}
                disabled={busyForId === a.id}
                title="Mute the rule"
              >
                <BellOff class="h-3.5 w-3.5" />
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                class="h-7 w-7"
                onclick={() => dismiss(a.id ?? "")}
                aria-label="Close"
              >
                <X class="h-3.5 w-3.5" />
              </Button>
            </div>
          </div>
        </div>
      </div>
    {/each}
  </div>
{/if}
