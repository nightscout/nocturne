<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import { bg, bgLabel, formatLocale } from "$lib/utils/formatting";
  import type {
    LoopPillData,
    PillInfoItem,
    AlertLevel,
  } from "$lib/types/status-pills";

  interface LoopPillProps {
    data: LoopPillData | null;
  }

  let { data }: LoopPillProps = $props();

  /** Format relative time (e.g., "3m ago") */
  function formatTimeAgo(mills: number): string {
    const now = Date.now();
    const diff = now - mills;
    const mins = Math.floor(diff / 60000);

    if (mins < 1) return "just now";
    if (mins < 60) return `${mins}m ago`;

    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours}h ago`;

    const days = Math.floor(hours / 24);
    return `${days}d ago`;
  }

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Last enacted action
    if (data.lastEnacted) {
      const timeAgo = formatTimeAgo(data.lastEnacted.time);

      let lead: string | undefined;
      let actionText = "";
      if (data.lastEnacted.bolusVolume) {
        lead = "Automatic Bolus";
        actionText = ` ${data.lastEnacted.bolusVolume}U`;
        if (data.lastEnacted.type === "cancel") {
          actionText += " (Temp Basal Canceled)";
        }
      } else if (data.lastEnacted.type === "cancel") {
        lead = "Temp Basal Canceled";
      } else if (data.lastEnacted.rate != null) {
        lead = "Temp Basal Started";
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
