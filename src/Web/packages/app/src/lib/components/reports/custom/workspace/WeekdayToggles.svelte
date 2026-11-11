<!--
  Seven toggles, Monday to Sunday, for the days a group covers. At least one day stays on.
-->
<script lang="ts">
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { WEEKDAYS, type Weekday } from "./vocabulary.js";

  interface Props {
    value: Weekday[];
    onchange: (days: Weekday[]) => void;
    label: string;
  }

  let { value, onchange, label }: Props = $props();

  function isWeekday(day: string): day is Weekday {
    return WEEKDAYS.some((w) => w.id === day);
  }
</script>

<ToggleGroup.Root
  type="multiple"
  variant="outline"
  size="xs"
  spacing={1}
  class="flex-wrap"
  aria-label={label}
  bind:value={
    () => value,
    (days: string[]) => {
      const picked = days.filter(isWeekday);
      if (picked.length > 0)
        onchange(WEEKDAYS.map((w) => w.id).filter((d) => picked.includes(d)));
    }
  }
>
  {#each WEEKDAYS as day (day.id)}
    <ToggleGroup.Item value={day.id} aria-label={day.label} data-lab-selected>
      {day.id}
    </ToggleGroup.Item>
  {/each}
</ToggleGroup.Root>
