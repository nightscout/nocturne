<script lang="ts">
  import RotateCcw from "lucide-svelte/icons/rotate-ccw";

  interface Props {
    isZoomed: boolean;
    brushXDomain: [Date, Date] | null;
    onResetZoom: () => void;
  }

  let { isZoomed, brushXDomain, onResetZoom }: Props = $props();
</script>

{#if isZoomed}
  <div
    class="flex items-center justify-between px-4 py-2 mb-2 bg-primary/5 border border-primary/20 rounded-lg"
  >
    <div class="flex items-center gap-2 text-sm text-primary">
      <span class="font-medium">Zoomed view</span>
      {#if brushXDomain}
        <span class="text-xs text-muted-foreground">
          {brushXDomain[0].toLocaleTimeString([], {
            hour: "numeric",
            minute: "2-digit",
          })} - {brushXDomain[1].toLocaleTimeString([], {
            hour: "numeric",
            minute: "2-digit",
          })}
        </span>
      {/if}
    </div>
    <button
      type="button"
      class="flex items-center gap-1 px-2 py-1 text-xs font-medium text-primary bg-primary/10 hover:bg-primary/20 rounded transition-colors"
      onclick={onResetZoom}
    >
      <RotateCcw size={12} />
      Reset zoom
    </button>
  </div>
{/if}
