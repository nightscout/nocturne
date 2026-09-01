<script lang="ts">
  import {
    getActiveAlerts,
    snoozeInstance,
    acknowledgeExcursion,
  } from "$api/generated/alerts.generated.remote";
  import type { ActiveExcursionResponse } from "$api-clients";
  import { Button } from "$lib/components/ui/button";
  import { Bell, BellOff, X } from "lucide-svelte";
  import { severity } from "./severity";
  import { formatTimeSince } from "./alertTime";
  import { Now } from "$lib/hooks/now.svelte";

  /**
   * App-wide fresh-fire toast. Reads the shared active-alerts surface; whenever
   * a new alert id appears (i.e. one we haven't shown before this session),
   * surface a top-center toast with Snooze / Dismiss / Mute-rule actions.
   *
   * The component intentionally does _not_ show every active alert — that's the
   * persistent banner's job (currently <see cref="AlertBanner"/>). This is for
   * the trust-critical "you should know about this RIGHT NOW" moment.
   *
   * Actions are optimistic: the card leaves the queue immediately and is
   * restored only if the command fails. Acknowledge additionally pushes a
   * single-flight override into the shared getActiveAlerts query so the banner
   * reflects it in the same round-trip.
   */

  // Toasts are appended whenever a new alert id appears; users dismiss them
  // explicitly. We don't auto-dismiss so the trust gesture is intentional.
  let queue = $state<ActiveExcursionResponse[]>([]);
  // Which ids we've already shown, so a refresh doesn't spawn duplicates. Kept
  // off $state: the effect below both reads and writes it, and nothing renders
  // from it.
  const seen = new Set<string>();
  // Reactive clock so each card's relative time ages while it sits on screen.
  // Toasts never auto-dismiss and existing queue items aren't replaced on
  // refresh, so without this the label would freeze at first render.
  const clock = new Now();
  const now = $derived(clock.current);

  const activeAlerts = getActiveAlerts();

  // The layout drives one shared poll of this query; react to whatever it
  // returns rather than running a second timer at a different cadence.
  $effect(() => {
    const list = activeAlerts.current ?? [];
    const fresh: ActiveExcursionResponse[] = [];
    for (const a of list) {
      const id = a.id ?? "";
      if (!id || seen.has(id) || a.acknowledgedAt) continue;
      seen.add(id);
      fresh.push(a);
    }
    if (fresh.length > 0) queue = [...fresh, ...queue];
    // Remove toasts that were acknowledged elsewhere (other tab, banner, etc.).
    // Assign only when a card actually drops: this effect reads `queue`, and
    // `filter` returns a new array even when nothing matched, so an
    // unconditional write re-dirties the effect's own dependency and loops.
    const ackedIds = new Set(
      list.filter((a) => a.acknowledgedAt).map((a) => a.id)
    );
    if (ackedIds.size > 0) {
      const remaining = queue.filter((a) => !ackedIds.has(a.id));
      if (remaining.length !== queue.length) queue = remaining;
    }
  });

  function dismiss(id: string): void {
    queue = queue.filter((a) => a.id !== id);
  }

  /**
   * Drop the card now, run the command, and restore it if the command fails.
   * `seen` already holds the id, so poll() won't resurface a rolled-back card.
   */
  async function optimistic(
    id: string,
    action: () => Promise<unknown>
  ): Promise<void> {
    const snapshot = queue;
    queue = queue.filter((a) => a.id !== id);
    try {
      await action();
    } catch {
      // Restoring the card is the report: it reappears, so the alert is still
      // outstanding and the action can be retried.
      queue = snapshot;
    }
  }

  function snooze(id: string, minutes: number): Promise<void> {
    return optimistic(id, () =>
      snoozeInstance({ instanceId: id, request: { minutes } })
    );
  }

  function ack(id: string): Promise<void> {
    return optimistic(id, () =>
      acknowledgeExcursion({
        excursionId: id,
        request: {},
      }).updates(
        activeAlerts.withOverride((current) =>
          (current ?? []).map((a) =>
            a.id === id ? { ...a, acknowledgedAt: new Date() } : a
          )
        )
      )
    );
  }

  // "Mute the rule" used to call toggleRule, which disables the rule outright,
  // tenant-wide, with no confirmation — a one-tap way to switch off a safety rule
  // at 3am, and one that silently re-enabled a rule already disabled. Snoozing is
  // the transient action; changing a rule now happens on the rule's own page,
  // where the effect is labelled.
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
            class="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded-full {severity(
              a.severity,
              'chip'
            )}"
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
                {formatTimeSince(a.startedAt, now)}
              </span>
            </div>
            <div class="mt-2 flex flex-wrap items-center gap-1">
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 5)}
              >
                5m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 15)}
              >
                15m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 30)}
              >
                30m
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                class="h-7 px-2 text-xs"
                onclick={() => snooze(a.id ?? "", 60)}
              >
                1h
              </Button>
              <!-- This one records the acknowledgement; the X beside it only
                   closes the card. They used to read "Dismiss" and an unlabelled
                   cross, which is the wrong pair of words for that difference. -->
              <Button
                type="button"
                variant="ghost"
                size="sm"
                class="h-7 px-2 text-xs ml-auto"
                onclick={() => ack(a.id ?? "")}
              >
                Acknowledge
              </Button>
              {#if a.alertRuleId}
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  class="h-7 px-2 text-xs"
                  href="/alerts/{a.alertRuleId}"
                  title="Open this rule's settings"
                  aria-label="Open settings for {a.ruleName ?? 'this rule'}"
                >
                  <BellOff class="h-3.5 w-3.5" />
                </Button>
              {/if}
              <Button
                type="button"
                variant="ghost"
                size="icon"
                class="h-7 w-7"
                onclick={() => dismiss(a.id ?? "")}
                aria-label="Close this notification without acknowledging"
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
