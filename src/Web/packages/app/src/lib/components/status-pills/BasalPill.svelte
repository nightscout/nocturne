<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import type {
    BasalPillData,
    PillInfoItem,
    AlertLevel,
  } from "$lib/types/status-pills";

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
      label: "Current Basal",
      value: data.display,
    });

    // Active profile
    if (data.activeProfile) {
      items.push({
        label: "Active Profile",
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
        label: "Active Temp Basal",
        value: tempText,
      });

      if (data.tempBasal.startTime) {
        items.push({
          label: "Active Temp Basal Start",
          value: new Date(data.tempBasal.startTime).toLocaleString(),
        });
      }

      items.push({
        label: "Active Temp Basal Duration",
        value: `${data.tempBasal.duration} mins`,
      });

      items.push({
        label: "Active Temp Basal Remaining",
        value: `${data.tempBasal.remaining} mins`,
      });

      // Show scheduled basal for reference
      items.push({
        label: "Basal Profile Value",
        value: `${data.scheduledBasal.toFixed(3)} U`,
      });
    }

    return items;
  });

  /** Build label with temp/combo indicators */
  const pillLabel = $derived.by(() => {
    let label = "BASAL";
    if (data?.isTempBasal) {
      label = "T: " + label;
    }
    if (data?.isComboActive) {
      label = "C" + (data?.isTempBasal ? "" : ": ") + label;
    }
    return label;
  });

  const level = $derived<AlertLevel>(data?.level ?? "none");
  const display = $derived(data?.display ?? "---U");
  const isStale = $derived(data?.isStale ?? false);
</script>

<StatusPill value={display} label={pillLabel} {info} {level} {isStale} />
