<script lang="ts">
  import type { ComponentProps } from "svelte";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import CalendarDayCell from "./CalendarDayCell.svelte";

  let cellProps: ComponentProps<typeof CalendarDayCell> = $props();
</script>

<Tooltip.Provider>
  <div class="h-24 w-32">
    <CalendarDayCell {...cellProps} />
  </div>
</Tooltip.Provider>
