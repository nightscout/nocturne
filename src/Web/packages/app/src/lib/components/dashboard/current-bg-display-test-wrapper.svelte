<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import CurrentBGDisplay from "./CurrentBGDisplay.svelte";

  interface Props {
    showPills?: boolean;
  }

  let { showPills = true }: Props = $props();
</script>

<Tooltip.Provider>
  <CurrentBGDisplay {showPills} />
</Tooltip.Provider>
