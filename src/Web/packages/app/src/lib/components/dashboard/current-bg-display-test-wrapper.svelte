<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import CurrentBGDisplay from "./CurrentBGDisplay.svelte";

  interface Props {
    showPills?: boolean;
  }

  let { showPills = true }: Props = $props();
</script>

<!-- The dashboard page is the container the mobile rail's breakpoint reads. -->
<Tooltip.Provider>
  <div class="@container">
    <CurrentBGDisplay {showPills} />
  </div>
</Tooltip.Provider>
