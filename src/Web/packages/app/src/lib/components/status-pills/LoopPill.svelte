<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import type {
    LoopPillData,
    PillInfoItem,
    AlertLevel,
  } from "$lib/types/status-pills";

  interface LoopPillProps {
    data: LoopPillData | null;
    units?: string;
  }

  let { data, units = "mmol/L" }: LoopPillProps = $props();

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

  /** Format BG value based on units */
  function formatBG(mgdl: number): string {
    if (units === "mmol/L" || units === "mmol") {
      return (mgdl / 18.0182).toFixed(1);
    }
    return Math.round(mgdl).toString();
  }

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Last enacted action
    if (data.lastEnacted) {
      const timeAgo = formatTimeAgo(data.lastEnacted.time);

      let actionText = "";
      if (data.lastEnacted.bolusVolume) {
        actionText = `<b>Automatic Bolus</b> ${data.lastEnacted.bolusVolume}U`;
        if (data.lastEnacted.type === "cancel") {
          actionText += " (Temp Basal Canceled)";
        }
      } else if (data.lastEnacted.type === "cancel") {
        actionText = "<b>Temp Basal Canceled</b>";
      } else if (data.lastEnacted.rate !== undefined) {
        actionText = `<b>Temp Basal Started</b> ${data.lastEnacted.rate.toFixed(2)}U/hour for ${data.lastEnacted.duration}m`;
      }

      if (data.lastEnacted.reason) {
        actionText += `, ${data.lastEnacted.reason}`;
      }

      // Add IOB/COB info from loop
      if (data.iob !== undefined) {
        actionText += `, IOB: ${data.iob.toFixed(2)}U`;
      }
      if (data.cob !== undefined) {
        actionText += `, COB: ${Math.round(data.cob)}g`;
      }

      // Add eventual BG
      if (data.eventualBG !== undefined) {
        actionText += `, Eventual BG: ${formatBG(data.eventualBG)}`;
      }

      items.push({
        label: timeAgo,
        value: actionText,
      });
    }

    // Error information
    if (data.status === "error" && data.failureReason) {
      items.push({
        label: "Error",
        value: `<span class="text-red-600">${data.failureReason}</span>`,
      });
    }

    return items;
  });

  /** Get status symbol */
  const statusSymbol = $derived.by((): string => {
    if (!data) return "⚠";

    switch (data.status) {
      case "enacted":
        return "⌁";
      case "recommendation":
        return "⏀";
      case "looping":
        return "↻";
      case "error":
        return "x";
      case "warning":
      default:
        return "⚠";
    }
  });

  /** Build label with loop name and symbol */
  const pillLabel = $derived.by(() => {
    const name = data?.loopName ?? "Loop";
    return `${name} ${statusSymbol}`;
  });

  /** Build display value with time and eventual BG */
  const display = $derived.by(() => {
    if (!data?.lastLoopTime) return null;

    const time = new Date(data.lastLoopTime).toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
    });

    if (data.eventualBG !== undefined) {
      return `${time} ↝ ${formatBG(data.eventualBG)}`;
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
