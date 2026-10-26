<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import { bg, bgLabel, formatLocale, relativeAge } from "#lib/utils/formatting.js";
  import type {
    LoopPillData,
    PillInfoItem,
    AlertLevel,
  } from "#lib/types/status-pills.js";

  interface LoopPillProps {
    data: LoopPillData | null;
  }

  let { data }: LoopPillProps = $props();

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Last enacted action
    if (data.lastEnacted) {
      const timeAgo = relativeAge(data.lastEnacted.time);

      let lead: string | undefined;
      let actionText = "";
      if (data.lastEnacted.bolusVolume) {
        lead = "Automatic bolus";
        actionText = ` ${data.lastEnacted.bolusVolume}U`;
        if (data.lastEnacted.type === "cancel") {
          actionText += " (Temp Basal Canceled)";
        }
      } else if (data.lastEnacted.type === "cancel") {
        lead = "Temp basal canceled";
      } else if (data.lastEnacted.rate != null) {
        lead = "Temp basal started";
        actionText = ` ${data.lastEnacted.rate.toFixed(2)}U/hour for ${data.lastEnacted.duration}m`;
      }

      if (data.lastEnacted.reason) {
        actionText += `, ${data.lastEnacted.reason}`;
      }

      // Add IOB/COB info from loop
      if (data.iob != null) {
        actionText += `, IOB: ${data.iob.toFixed(2)}U`;
      }
      if (data.cob != null) {
        actionText += `, COB: ${Math.round(data.cob)}g`;
      }

      // Add eventual BG
      if (data.eventualBG != null) {
        actionText += `, Eventual BG: ${bg(data.eventualBG)}`;
      }

      items.push({
        label: timeAgo,
        lead,
        value: actionText,
      });
    }

    // Error information
    if (data.status === "error" && data.failureReason) {
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
      case "enacted":
        return "enacted";
      case "recommendation":
        return "suggested";
      case "looping":
        return "looping";
      case "error":
        return "error";
      default:
        return "not recent";
    }
  });

  const pillLabel = $derived(`${data?.loopName ?? "Loop"} · ${statusWord}`);

  /** Build display value with time and eventual BG */
  const display = $derived.by(() => {
    if (!data?.lastLoopTime) return null;

    const time = new Date(data.lastLoopTime).toLocaleTimeString(
      formatLocale(),
      {
        hour: "2-digit",
        minute: "2-digit",
      }
    );

    if (data.eventualBG != null) {
      return `${time} · eventual ${bg(data.eventualBG)} ${bgLabel()}`;
    }

    return time;
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
