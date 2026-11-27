<script lang="ts">
  import { untrack } from "svelte";
  import { plural } from "#lib/utils/plural.js";
  import { NotificationUrgency, TrackerMode } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { enumValue } from "#lib/components/ui/enum-value.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import Bell from "@lucide/svelte/icons/bell";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import {
    joinHours,
    splitHours,
    thresholdIssue,
    ThresholdUnits,
    type ThresholdSide,
    type ThresholdUnit,
  } from "./threshold-time";
  import type { TrackerNotification } from "./types";

  interface Props {
    notification: TrackerNotification;
    /** The row's place in the list, for the ids of its fields. */
    index: number;
    mode: TrackerMode;
    /** The tracker's lifespan, which a threshold before the end counts back from. */
    lifespanHours: number | undefined;
    /** Whether the viewer may open the alert rule that delivers this threshold. */
    canOpenRule: boolean;
    onChange: (patch: Partial<TrackerNotification>) => void;
    onRemove: () => void;
  }

  let { notification, index, mode, lifespanHours, canOpenRule, onChange, onRemove }: Props =
    $props();

  const URGENCIES = [
    NotificationUrgency.Info,
    NotificationUrgency.Warn,
    NotificationUrgency.Hazard,
    NotificationUrgency.Urgent,
  ];

  const URGENCY_COLORS: Record<NotificationUrgency, string> = {
    [NotificationUrgency.Info]: "text-severity-info",
    [NotificationUrgency.Warn]: "text-severity-warn",
    [NotificationUrgency.Hazard]: "text-severity-hazard",
    [NotificationUrgency.Urgent]: "text-severity-urgent",
  };

  function urgencyLabel(urgency: NotificationUrgency): string {
    switch (urgency) {
      case NotificationUrgency.Warn:
        return "Warning";
      case NotificationUrgency.Hazard:
        return "Hazard";
      case NotificationUrgency.Urgent:
        return "Urgent";
      default:
        return "Info";
    }
  }

  function unitLabel(unit: ThresholdUnit, amount: number): string {
    if (unit === ThresholdUnits.Days) {
      // @wc-context: threshold-unit
      return plural(amount, ["day", "days"]);
    }
    // @wc-context: threshold-unit
    return plural(amount, ["hour", "hours"]);
  }

  function sideLabel(side: ThresholdSide): string {
    const before = side === "before";
    if (mode === TrackerMode.Event) {
      // @wc-include
      return before ? "before the date" : "after the date";
    }
    // @wc-include
    return before ? "before it ends" : "after it starts";
  }

  // The amount is the person's own text, and the unit their own choice: 48 hours stays "48 hours"
  // while they edit it, though the stored hours would read back as 2 days.
  const stored = untrack(() => splitHours(notification.hours, mode));
  let text = $state(stored.amount === undefined ? "" : String(stored.amount));
  let unit = $state<ThresholdUnit>(stored.unit);
  let side = $state<ThresholdSide>(stored.side);

  const entered = () =>
    joinHours({ amount: text.trim() === "" ? undefined : Number(text), unit, side });

  // The hours can change from outside the row: a new lifespan moves the thresholds at its end.
  $effect(() => {
    const hours = notification.hours;
    untrack(() => {
      if (hours === entered()) return;
      const next = splitHours(hours, mode);
      text = next.amount === undefined ? "" : String(next.amount);
      unit = next.unit;
      side = next.side;
    });
  });

  function commit(): void {
    onChange({ hours: entered() });
  }

  // A tracker with no lifespan has no end to count back from.
  const sides = $derived<ThresholdSide[]>(
    mode === TrackerMode.Duration && lifespanHours === undefined && side !== "before"
      ? ["after"]
      : ["before", "after"],
  );

  const issue = $derived(thresholdIssue(notification.hours, mode, lifespanHours));
</script>

<div class="space-y-3 rounded-lg border bg-muted/30 p-3" data-testid="tracker-threshold">
  <div class="flex flex-wrap items-end gap-2">
    <div class="w-28 space-y-1">
      <Label for="threshold-level-{index}" size="sm" variant="muted">Level</Label>
      <Select.Root
        type="single"
        value={notification.urgency}
        onValueChange={(v) => {
          const urgency = enumValue(NotificationUrgency, v);
          if (urgency) onChange({ urgency });
        }}
      >
        <Select.Trigger id="threshold-level-{index}" class="w-full">
          <span class={URGENCY_COLORS[notification.urgency]}>
            {urgencyLabel(notification.urgency)}
          </span>
        </Select.Trigger>
        <Select.Content>
          {#each URGENCIES as urgency (urgency)}
            <Select.Item value={urgency}>
              <span class={URGENCY_COLORS[urgency]}>{urgencyLabel(urgency)}</span>
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>

    <div class="space-y-1">
      <Label for="threshold-amount-{index}" size="sm" variant="muted">Time</Label>
      <div class="flex flex-wrap items-center gap-2">
        <Input
          id="threshold-amount-{index}"
          type="number"
          inputmode="decimal"
          min="0"
          step={unit === ThresholdUnits.Days ? 0.5 : 1}
          class="w-20 tabular-nums"
          aria-invalid={issue !== null}
          value={text}
          oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
            text = e.currentTarget.value;
            commit();
          }}
        />
        <Select.Root
          type="single"
          value={unit}
          onValueChange={(v) => {
            unit = v === ThresholdUnits.Hours ? ThresholdUnits.Hours : ThresholdUnits.Days;
            commit();
          }}
        >
          <Select.Trigger class="w-24" aria-label="Unit" data-testid="threshold-unit-{index}">
            {unitLabel(unit, Number(text))}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="hours" label={unitLabel(ThresholdUnits.Hours, Number(text))} />
            <Select.Item value="days" label={unitLabel(ThresholdUnits.Days, Number(text))} />
          </Select.Content>
        </Select.Root>
        <Select.Root
          type="single"
          value={side}
          onValueChange={(v) => {
            side = v === "before" ? "before" : "after";
            commit();
          }}
        >
          <Select.Trigger class="w-40" aria-label="When" data-testid="threshold-side-{index}">
            {sideLabel(side)}
          </Select.Trigger>
          <Select.Content>
            {#each sides as option (option)}
              <Select.Item value={option} label={sideLabel(option)} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    </div>

    <div class="ml-auto flex items-center gap-1">
      <!-- New tab: this editor is inside a form, so in-place navigation would discard the tracker
           edits made so far. -->
      {#if notification.alertRuleId && canOpenRule}
        <Button
          variant="ghost-muted"
          href="/alerts/{notification.alertRuleId}"
          target="_blank"
          rel="noopener"
          title="Configure delivery channels for this threshold"
        >
          <Bell class="h-4 w-4" />
          Channels
        </Button>
      {/if}
      <Button variant="ghost-destructive" size="icon" type="button" onclick={onRemove}>
        <Trash2 class="h-4 w-4" />
        <span class="sr-only">Remove notification</span>
      </Button>
    </div>
  </div>

  {#if issue}
    <p class="text-sm text-destructive" role="alert">
      {#if issue === "no-lifespan"}
        Set how long the tracker lasts before you use a time before its end.
      {:else}
        This time is before the tracker starts. Use a time shorter than the tracker lasts.
      {/if}
    </p>
  {/if}

  <div class="space-y-1">
    <Label for="threshold-message-{index}" size="sm" variant="muted">Message (optional)</Label>
    <Input
      id="threshold-message-{index}"
      value={notification.description ?? ""}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
        onChange({ description: e.currentTarget.value })}
      placeholder="The text of the notification"
    />
  </div>
</div>
