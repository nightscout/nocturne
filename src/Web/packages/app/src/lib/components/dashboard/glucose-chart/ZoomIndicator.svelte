<script lang="ts">
  import RotateCcw from "lucide-svelte/icons/rotate-ccw";
  import { Button } from "$lib/components/ui/button";
  import { time } from "$lib/utils/formatting";

  interface Props {
    isZoomed: boolean;
    brushXDomain: [Date, Date] | null;
    onResetZoom: () => void;
  }

  let { isZoomed, brushXDomain, onResetZoom }: Props = $props();
</script>

{#if isZoomed}
  <div
    class="flex items-center justify-between px-4 py-2 mb-2 bg-primary/5 border border-primary/20 rounded-lg print:hidden"
  >
    <div class="flex items-center gap-2 text-sm text-primary">
      <span class="font-medium">Zoomed view</span>
      {#if brushXDomain}
        <span class="text-xs text-muted-foreground">
          {time(brushXDomain[0])} - {time(brushXDomain[1])}
        </span>
      {/if}
    </div>
    <Button variant="ghost" size="sm" class="flex items-center gap-1 text-xs font-medium text-primary" onclick={onResetZoom}>
      <RotateCcw size={12} />
      Reset zoom
    </Button>
  </div>
{/if}
