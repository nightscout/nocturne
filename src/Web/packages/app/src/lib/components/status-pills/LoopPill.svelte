<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import { bg, bgLabel, relativeAge, time } from "#lib/utils/formatting.js";
  import {
    LoopStatus,
    type LoopPillData,
    type PillInfoItem,
    type AlertLevel,
  } from "#lib/types/status-pills.js";

  interface LoopPillProps {
    data: LoopPillData | null;
    /** "nightscout" reads as cgm-remote-monitor's loop pill: "Loop ↻" and "2m ago ↝ 5.6". */
    variant?: "default" | "nightscout";
  }

  let { data, variant = "default" }: LoopPillProps = $props();

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Last enacted action
    const enacted = data.lastEnacted;
    if (enacted) {
      let lead: string | undefined;
      let value = "";
      if (enacted.bolusVolume) {
        lead = "Automatic bolus";
        if (enacted.type === "cancel") {
          // @wc-include
          value = `${enacted.bolusVolume}U (temp basal canceled)`;
        } else {
          value = `${enacted.bolusVolume}U`;
        }
      } else if (enacted.type === "cancel") {
        lead = "Temp basal canceled";
      } else if (enacted.rate != null) {
        lead = "Temp basal started";
        // @wc-include
        value = `${enacted.rate.toFixed(2)}U/h for ${enacted.duration} mins`;
      }

      items.push({ label: relativeAge(enacted.time), lead, value });
      if (enacted.reason) items.push({ label: "Reason", value: enacted.reason });
      if (data.iob != null) items.push({ label: "IOB", value: `${data.iob.toFixed(2)}U` });
      if (data.cob != null) items.push({ label: "COB", value: `${Math.round(data.cob)}g` });
      if (data.eventualBG != null) items.push({ label: "Eventual BG", value: bg(data.eventualBG) });
    }

    // Error information
    if (data.status === LoopStatus.Error && data.failureReason) {
      items.push({
        label: "Error",
        value: data.failureReason,
        tone: "destructive",
      });
    }

    return items;
  });

  const statusWord = $derived.by((): string => {
    switch (data?.status) {
      case LoopStatus.Enacted:
        // @wc-include
        // @wc-context: loop-status
        return "enacted";
      case LoopStatus.Recommendation:
        // @wc-include
        // @wc-context: loop-status
        return "suggested";
      case LoopStatus.Looping:
        // @wc-include
        // @wc-context: loop-status
        return "looping";
      case LoopStatus.Error:
        // @wc-include
        // @wc-context: loop-status
        return "error";
      default:
        // @wc-include
        // @wc-context: loop-status
        return "not recent";
    }
  });

  // The symbols lib/plugins/loop.js shows for each status.
  const nightscoutSymbol = $derived.by((): string => {
    switch (data?.status) {
      case LoopStatus.Enacted:
        return "⌁";
      case LoopStatus.Recommendation:
        return "⏀";
      case LoopStatus.Looping:
        return "↻";
      case LoopStatus.Warning:
        return "⚠";
      case LoopStatus.Error:
        return "x";
      default:
        return "?";
    }
  });

  const pillLabel = $derived(
    variant === "nightscout"
      ? `${data?.loopName ?? "Loop"} ${nightscoutSymbol}`
      : `${data?.loopName ?? "Loop"} · ${statusWord}`
  );

  /** Build display value with time and eventual BG */
  const display = $derived.by(() => {
    if (!data?.lastLoopTime) return null;

    if (variant === "nightscout") {
      const age = relativeAge(data.lastLoopTime);
      return data.eventualBG != null ? `${age} ↝ ${bg(data.eventualBG)}` : age;
    }

    const at = time(data.lastLoopTime);

    if (data.eventualBG != null) {
      return `${at} · eventual ${bg(data.eventualBG)} ${bgLabel()}`;
    }

    return at;
  });

  const level = $derived<AlertLevel>(data?.level ?? "none");
  const isStale = $derived(data?.isStale ?? !data?.lastLoopTime);
</script>

<StatusPill
  value={display ?? "---"}
  label={pillLabel}
  {info}
  {level}
  {isStale}
/>
