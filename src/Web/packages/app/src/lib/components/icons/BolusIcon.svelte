<script lang="ts">
  /**
   * Bolus marker icon — a triangle pointing down at the baseline. Used for bolus
   * treatment markers in legends and stat cards; the chart draws the same shape
   * from <see>marker-shapes</see>. When isOverride is true it is taller, matching
   * the chart's manual-override marker.
   */
  import type { IconProps } from "./types";
  import { trianglePoints } from "./marker-shapes";

  interface BolusIconProps extends IconProps {
    /** Whether this bolus was a manual override (taller triangle) */
    isOverride?: boolean;
  }

  let {
    size = 16,
    color = "var(--insulin-bolus)",
    isOverride = false,
    class: className = "",
    ...rest
  }: BolusIconProps = $props();

  // Scale factor based on default 12px reference size
  const scale = $derived(size / 12);
  const radius = $derived(6 * scale);
  const height = $derived(isOverride ? size / 2 + 2 : size / 2);
</script>

<svg
  width={size}
  height={size / 2 + 2}
  viewBox="0 0 {size} {size / 2 + 2}"
  class={className}
  {...rest}
>
  <polygon
    points={trianglePoints("down", radius, height, size / 2, size / 2 + 2)}
    fill={color}
  />
</svg>
