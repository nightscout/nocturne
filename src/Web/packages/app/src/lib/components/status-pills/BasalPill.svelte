<script lang="ts">
  import { formatMediumDateTime } from "#lib/utils/formatting.js";
  import StatusPill from "./StatusPill.svelte";
  import type {
    BasalPillData,
    PillInfoItem,
    AlertLevel,
  } from "#lib/types/status-pills.js";

  interface BasalPillProps {
    data: BasalPillData | null;
  }

  let { data }: BasalPillProps = $props();

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Current basal info
    items.push({
      label: "Current basal",
      value: data.display,
    });

    // Active profile
    if (data.activeProfile) {
      items.push({
        label: "Active profile",
        value: data.activeProfile,
      });
    }

    // Temp basal details
    if (data.tempBasal && data.isTempBasal) {
      items.push({ label: "------------", value: "" });

      const tempText =
        data.tempBasal.percent !== undefined
          ? `${data.tempBasal.percent > 0 ? "+" : ""}${data.tempBasal.percent}%`
          : data.tempBasal.rate !== undefined
            ? `${data.tempBasal.rate}U/h`
            : "";

      items.push({
        label: "Active temp basal",
        value: tempText,
      });

      if (data.tempBasal.startTime) {
        items.push({
          label: "Active temp basal start",
          value: formatMediumDateTime(data.tempBasal.startTime),
        });
      }

      items.push({
        label: "Active temp basal duration",
        value: `${data.tempBasal.duration} mins`,
      });

      items.push({
        label: "Active temp basal remaining",
        value: `${data.tempBasal.remaining} mins`,
      });

      // Show scheduled basal for reference
      items.push({
        label: "Basal profile value",
        value: `${data.scheduledBasal.toFixed(3)} U`,
      });
    }

    return items;
  });

  const pillLabel = $derived.by(() => {
    const label = data?.isTempBasal ? "Temp basal" : "Basal";
    return data?.isComboActive ? `${label} · combo` : label;
  });

  const level = $derived<AlertLevel>(data?.level ?? "none");
  const display = $derived(data?.display ?? "---U");
  const isStale = $derived(data?.isStale ?? false);
</script>

<StatusPill value={display} label={pillLabel} {info} {level} {isStale} />
