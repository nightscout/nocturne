<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import { Loader2, Info, History, AlertCircle } from "lucide-svelte";
  import { replay } from "$api/generated/alertReplays.generated.remote";
  import { AlertRuleSeverity } from "$api-clients";

  interface ReplayEvent {
    at?: Date | string;
    ruleId?: string;
    ruleName?: string;
    severity?: AlertRuleSeverity | string;
  }

  interface ReplayResult {
    windowStart?: Date | string;
    windowEnd?: Date | string;
    events?: ReplayEvent[];
    limitations?: string;
  }

  interface Props {
    open: boolean;
  }

  let { open = $bindable() }: Props = $props();

  const browserTimezone =
    typeof Intl !== "undefined"
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : "UTC";

  let date = $state<string>("");
  let timezone = $state<string>(browserTimezone);
  let running = $state(false);
  let runError = $state<string | null>(null);
  let result = $state<ReplayResult | null>(null);

  async function handleRun() {
    if (running) return;
    running = true;
    runError = null;
    result = null;
    try {
      const res = await replay({
        date: date ? date : null,
        timezone: timezone || null,
      });
      result = (res ?? null) as ReplayResult | null;
    } catch (err) {
      runError =
        err instanceof Error
          ? err.message
          : "Failed to run replay. Please try again.";
    } finally {
      running = false;
    }
  }

  function severityLabel(s: AlertRuleSeverity | string | undefined): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "Critical";
      case AlertRuleSeverity.Warning:
        return "Warning";
      case AlertRuleSeverity.Info:
        return "Info";
      default:
        return s ?? "";
    }
  }

  function severityClass(s: AlertRuleSeverity | string | undefined): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "bg-destructive text-destructive-foreground";
      case AlertRuleSeverity.Warning:
        return "bg-amber-500/15 text-amber-700 dark:text-amber-400 border border-amber-500/30";
      case AlertRuleSeverity.Info:
        return "bg-blue-500/15 text-blue-700 dark:text-blue-400 border border-blue-500/30";
      default:
        return "bg-muted text-muted-foreground";
    }
  }

  function formatTime(at: Date | string | undefined, tz: string): string {
    if (!at) return "";
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return "";
    try {
      return new Intl.DateTimeFormat(undefined, {
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        timeZone: tz || undefined,
      }).format(d);
    } catch {
      return d.toISOString();
    }
  }

  function formatHourBucket(at: Date | string | undefined, tz: string): string {
    if (!at) return "";
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return "";
    try {
      return new Intl.DateTimeFormat(undefined, {
        weekday: "short",
        month: "short",
        day: "numeric",
        hour: "2-digit",
        timeZone: tz || undefined,
      }).format(d);
    } catch {
      return d.toISOString();
    }
  }

  function formatRange(
    start: Date | string | undefined,
    end: Date | string | undefined,
    tz: string,
  ): string {
    const s = start ? new Date(start as string | Date) : null;
    const e = end ? new Date(end as string | Date) : null;
    if (!s || !e || Number.isNaN(s.getTime()) || Number.isNaN(e.getTime()))
      return "";
    try {
      const fmt = new Intl.DateTimeFormat(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        timeZone: tz || undefined,
      });
      return `${fmt.format(s)} — ${fmt.format(e)}`;
    } catch {
      return `${s.toISOString()} — ${e.toISOString()}`;
    }
  }

  // Group events by hour bucket label for clearer chronological display.
  let groupedEvents = $derived.by(() => {
    const events = result?.events ?? [];
    const tz = timezone || browserTimezone;
    const groups: { label: string; events: ReplayEvent[] }[] = [];
    let currentLabel: string | null = null;
    for (const ev of events) {
      const label = formatHourBucket(ev.at, tz);
      if (label !== currentLabel) {
        groups.push({ label, events: [] });
        currentLabel = label;
      }
      groups[groups.length - 1].events.push(ev);
    }
    return groups;
  });

  let hasRun = $derived(result !== null);
  let isEmpty = $derived(hasRun && (result?.events?.length ?? 0) === 0);
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <History class="h-4 w-4" />
        Replay alert rules
      </Dialog.Title>
      <Dialog.Description>
        See which events the currently enabled rules would have produced over
        a window. Replay does not deliver any notifications.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-2">
      <div class="grid gap-3 sm:grid-cols-2">
        <div class="space-y-1.5">
          <Label for="replay-date">Date</Label>
          <Input id="replay-date" type="date" bind:value={date} />
          <p class="text-xs text-muted-foreground">
            Leave blank to replay the last 24 hours.
          </p>
        </div>
        <div class="space-y-1.5">
          <Label for="replay-tz">Timezone</Label>
          <Input
            id="replay-tz"
            type="text"
            bind:value={timezone}
            placeholder={browserTimezone}
          />
          <p class="text-xs text-muted-foreground">
            Defaults to your browser timezone.
          </p>
        </div>
      </div>

      <div class="flex justify-end">
        <Button onclick={handleRun} disabled={running}>
          {#if running}
            <Loader2 class="h-4 w-4 mr-2 animate-spin" />
            Running…
          {:else}
            Run replay
          {/if}
        </Button>
      </div>

      {#if runError}
        <div
          class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="h-4 w-4 mt-0.5 flex-none" />
          <p>{runError}</p>
        </div>
      {/if}

      {#if hasRun && result}
        <div class="space-y-3">
          {#if result.windowStart && result.windowEnd}
            <p class="text-xs text-muted-foreground">
              Window: {formatRange(
                result.windowStart,
                result.windowEnd,
                timezone || browserTimezone,
              )}
            </p>
          {/if}

          {#if isEmpty}
            <div
              class="rounded-md border bg-muted/30 px-4 py-6 text-center text-sm text-muted-foreground"
            >
              No events would have fired in this window.
            </div>
          {:else}
            <div
              class="max-h-80 overflow-y-auto rounded-md border divide-y"
            >
              {#each groupedEvents as group, gi (gi)}
                <div class="bg-muted/20 px-3 py-1.5 text-xs font-medium text-muted-foreground sticky top-0">
                  {group.label}
                </div>
                {#each group.events as ev, ei (gi + ":" + ei)}
                  <div class="flex items-center gap-3 px-3 py-2 text-sm">
                    <span class="font-mono text-xs text-muted-foreground tabular-nums w-20 shrink-0">
                      {formatTime(ev.at, timezone || browserTimezone)}
                    </span>
                    <Badge class={severityClass(ev.severity)}>
                      {severityLabel(ev.severity)}
                    </Badge>
                    <span class="flex-1 min-w-0 truncate">
                      {ev.ruleName ?? "(unnamed rule)"}
                    </span>
                  </div>
                {/each}
              {/each}
            </div>
          {/if}

          {#if result.limitations}
            <div
              class="flex items-start gap-2 rounded-md border bg-muted/30 p-3 text-xs text-muted-foreground"
            >
              <Info class="h-4 w-4 mt-0.5 flex-none" />
              <p class="whitespace-pre-wrap">{result.limitations}</p>
            </div>
          {/if}
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (open = false)}>Close</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
