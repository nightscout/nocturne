<script lang="ts">
  import * as Tooltip from "$lib/components/ui/tooltip";
  import HelpDashboard from "./HelpDashboard.svelte";
  let { allowedSettings }: { allowedSettings: string[] } = $props();
</script>

<Tooltip.Provider>
  <div class="bg-background text-foreground p-6">
    <HelpDashboard {allowedSettings} />
  </div>
</Tooltip.Provider>
