<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import HelpDashboard from "./HelpDashboard.svelte";
  let { allowedSettings, width }: { allowedSettings: string[]; width?: number } = $props();
</script>

<Tooltip.Provider>
  <div class="bg-background text-foreground p-6 max-w-[var(--demo-width)]" style:--demo-width={width ? `${width}px` : "100%"}>
    <HelpDashboard {allowedSettings} />
  </div>
</Tooltip.Provider>
