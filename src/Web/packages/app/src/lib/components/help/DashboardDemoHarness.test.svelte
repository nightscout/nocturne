<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import HelpDashboard from "./HelpDashboard.svelte";
  let { allowedSettings, width }: { allowedSettings: string[]; width?: number } = $props();
</script>

<Tooltip.Provider>
  <div class="bg-background text-foreground p-6" style:max-width={width ? `${width}px` : undefined}>
    <HelpDashboard {allowedSettings} />
  </div>
</Tooltip.Provider>
