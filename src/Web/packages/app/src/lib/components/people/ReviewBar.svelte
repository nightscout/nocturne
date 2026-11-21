<script lang="ts">
  import { Progress } from "#lib/components/ui/progress/index.js";
  import { textureBackground } from "@nocturne/charts/core";

  interface Props {
    /** The in-range percent, as the API states it. */
    percent: number;
    /** Hatched when the period does not meet the reliability criteria. */
    reliable: boolean;
    class?: string;
  }

  const { percent, reliable, class: className = "" }: Props = $props();

  const hatch = $derived(reliable ? null : textureBackground("limited-data"));
</script>

<Progress
  value={percent}
  tone="in-range"
  hatched={hatch !== null}
  style={hatch ? `--hatch: ${hatch.image}; --hatch-size: ${hatch.size}` : undefined}
  aria-label="Time in range"
  class={className}
  data-testid="review-bar"
  data-hatched={hatch ? "" : undefined}
/>
