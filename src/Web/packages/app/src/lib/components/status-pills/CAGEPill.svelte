<script lang="ts">
  import StatusPill from "./StatusPill.svelte";
  import type {
    CAGEPillData,
    PillInfoItem,
    AlertLevel,
  } from "$lib/types/status-pills";

  interface CAGEPillProps {
    data: CAGEPillData | null;
    /** Maximum cannula life in hours (default 72) */
    maxLifeHours?: number;
  }

  let { data, maxLifeHours = 72 }: CAGEPillProps = $props();

  /** Format time remaining */
  function formatTimeRemaining(hours: number): string {
    if (hours <= 0) return "Overdue";

    const days = Math.floor(hours / 24);
    const remainingHours = Math.round(hours % 24);

    if (days > 0) {
      return `${days}d ${remainingHours}h`;
    }
    return `${remainingHours}h`;
  }

  /** Build info items for the popover */
  const info = $derived.by((): PillInfoItem[] => {
    if (!data) return [];

    const items: PillInfoItem[] = [];

    // Insertion time
    if (data.treatmentDate) {
      const insertedDate = new Date(data.treatmentDate);
      items.push({
        label: "Inserted",
        value: insertedDate.toLocaleString([], {
          month: "short",
          day: "numeric",
          year: "numeric",
          hour: "2-digit",
          minute: "2-digit",
        }),
      });
    }

    // Current age with more detail
    if (data.days !== undefined && data.hours !== undefined) {
      let ageDisplay = "";
      if (data.days > 0) {
        ageDisplay = `${data.days} day${data.days !== 1 ? "s" : ""}, ${data.hours} hour${data.hours !== 1 ? "s" : ""}`;
      } else {
        ageDisplay = `${data.age} hour${data.age !== 1 ? "s" : ""}`;
      }
      items.push({ label: "Age", value: ageDisplay });
    }

    // Time remaining (calculated from maxLifeHours)
    const timeRemaining = data.timeRemaining ?? maxLifeHours - data.age;
    if (timeRemaining !== undefined) {
      const remainingStr = formatTimeRemaining(timeRemaining);
      const remainingClass =
        timeRemaining <= 0
          ? "text-red-600 font-bold"
          : timeRemaining <= 6
            ? "text-yellow-600"
            : "";
      items.push({
        label: "Time Remaining",
        value: remainingClass
          ? `<span class="${remainingClass}">${remainingStr}</span>`
          : remainingStr,
      });
    }

    // Notes from treatment
    if (data.notes) {
      items.push({ label: "------------", value: "" });
      items.push({ label: "Notes", value: data.notes });
    }

    // Alert thresholds info
    items.push({ label: "------------", value: "" });
    items.push({
      label: "Recommended Change",
      value: `Every ${maxLifeHours}h (${Math.floor(maxLifeHours / 24)} days)`,
    });

    return items;
  });

  const level = $derived<AlertLevel>(data?.level ?? "none");
  const display = $derived(data?.display ?? "n/a");
  const isStale = $derived(data?.isStale ?? !data?.treatmentDate);
</script>

<StatusPill value={display} label="CAGE" {info} {level} {isStale} />
