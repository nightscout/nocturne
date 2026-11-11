<!--
  Seven toggles, Monday to Sunday, for the days a group covers. At least one day stays on.
-->
<script lang="ts">
  import { weekdayName } from "./vocabulary-words.svelte.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import type { Weekday } from "./catalogue-vocabulary.js";

  interface Props {
    value: Weekday[];
    onchange: (days: Weekday[]) => void;
    label: string;
    /** The weekdays in order: the catalogue's. */
    weekdays: readonly Weekday[];
  }

  let { value, onchange, label, weekdays }: Props = $props();
</script>

<ToggleGroup.Root
  type="multiple"
  variant="choice"
  size="xs"
  spacing={1}
  class="flex-wrap"
  aria-label={label}
  bind:value={
    () => value,
    (days: string[]) => {
      const picked = days.filter((d) => weekdays.includes(d));
      if (picked.length > 0) onchange(weekdays.filter((d) => picked.includes(d)));
    }
  }
>
  {#each weekdays as day (day)}
    <ToggleGroup.Item value={day} aria-label={weekdayName(day, weekdays)}>
      {weekdayName(day, weekdays, "short")}
    </ToggleGroup.Item>
  {/each}
</ToggleGroup.Root>
