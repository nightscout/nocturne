<!--
  The key under an hour strip, each swatch drawn as its mark is: a bar, a line, a dashed outline, a
  faded bar, a dot on its spread bar (filled, or hollow on a faded dashed bar), a diamond on a dashed
  bar for the compared range (filled, or hollow), or a step.
-->
<script lang="ts" module>
  export interface StripKeyItem {
    mark: "bar" | "band" | "line" | "reference" | "compared" | "sparse" | "dot" | "dot-hollow" | "dot-compared" | "dot-compared-hollow" | "step";
    label: string;
    color?: string;
    /** The fill opacity of the mark it names, where that differs from the kind's usual. */
    fill?: number;
  }
</script>

<script lang="ts">
  import { BAND_FILL, SPARSE_BAR_FILL } from "./loop-health-view";

  let { items }: { items: StripKeyItem[] } = $props();
</script>

<ul class="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-xs text-muted-foreground">
  {#each items as item (item.label)}
    <li class="flex items-center gap-1.5">
      <svg viewBox="0 0 16 12" aria-hidden="true" class="inline-block h-3 w-4 shrink-0">
        {#if item.mark === "dot"}
          <line x1="8" y1="0.5" x2="8" y2="11.5" stroke={item.color} stroke-width="2" stroke-linecap="round" />
          <circle cx="8" cy="6" r="3.5" fill={item.color} stroke={item.color} stroke-width="1.5" />
        {:else if item.mark === "dot-hollow"}
          <line x1="8" y1="0.5" x2="8" y2="11.5" stroke={item.color} stroke-opacity="0.5" stroke-width="2" stroke-dasharray="3 2" />
          <circle cx="8" cy="6" r="3.5" fill="var(--background)" stroke={item.color} stroke-width="1.5" />
        {:else if item.mark === "dot-compared" || item.mark === "dot-compared-hollow"}
          <line x1="8" y1="0.5" x2="8" y2="11.5" stroke="var(--foreground)" stroke-opacity="0.85" stroke-width="1.25" stroke-dasharray="3 2" />
          <path
            d="M8,2L12,6L8,10L4,6Z"
            fill={item.mark === "dot-compared" ? "var(--foreground)" : "var(--background)"}
            fill-opacity={item.mark === "dot-compared" ? 0.85 : 1}
            stroke="var(--foreground)"
            stroke-opacity="0.85"
            stroke-width="1.25"
          />
        {:else if item.mark === "step"}
          <path d="M0,9H8V3H16" fill="none" stroke="var(--foreground)" stroke-opacity="0.5" stroke-width="2" />
        {:else if item.mark === "line"}
          <line x1="0" y1="6" x2="16" y2="6" stroke={item.color} stroke-width="2" />
        {:else if item.mark === "reference"}
          <line x1="0" y1="6" x2="16" y2="6" stroke="var(--foreground)" stroke-opacity="0.4" stroke-width="1" />
        {:else if item.mark === "compared"}
          <rect x="1" y="1" width="14" height="10" rx="1" fill="none" stroke="var(--foreground)" stroke-opacity="0.85" stroke-width="1.25" stroke-dasharray="3 2" />
        {:else if item.mark === "sparse"}
          <rect x="1" y="1" width="14" height="10" rx="1" fill={item.color} fill-opacity={item.fill ?? SPARSE_BAR_FILL} stroke={item.color} stroke-width="1" />
        {:else}
          <rect x="1" y="1" width="14" height="10" rx="1" fill={item.color} fill-opacity={item.fill ?? (item.mark === "band" ? BAND_FILL : 1)} />
        {/if}
      </svg>
      <span>{item.label}</span>
    </li>
  {/each}
</ul>
