<script lang="ts">
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { VALUE_TONE } from "#lib/components/status-pills/tracker-pill-style.svelte.js";

  interface Props {
    /** Hides the words visually where they do not fit; the icon stays and a screen reader still reads them. */
    iconOnly?: boolean;
  }

  const { iconOnly = false }: Props = $props();
</script>

<span
  class="inline-flex shrink-0 items-center gap-1 text-xs font-medium {VALUE_TONE.warn}"
  data-testid="review-limited"
  title={iconOnly ? "Limited data" : undefined}
>
  <TriangleAlert class="size-3.5" aria-hidden="true" />
  <span class={iconOnly ? "sr-only" : ""}>Limited data</span>
</span>
