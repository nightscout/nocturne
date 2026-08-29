<script lang="ts">
  /**
   * Carbs marker icon — a triangle pointing up at the baseline. Used for carb
   * treatment markers in legends and stat cards; the chart draws the same shape
   * from <see>marker-shapes</see>. Complements BolusIcon, which points down at
   * the same baseline.
   */
  import type { IconProps } from "./types";
  import { trianglePoints } from "./marker-shapes";

  let {
    size = 16,
    color = "var(--carbs)",
    class: className = "",
    ...rest
  }: IconProps = $props();

  // Scale factor based on default 12px reference size
  const scale = $derived(size / 12);
  const radius = $derived(6 * scale);
</script>

<svg
  width={size}
  height={size / 2 + 2}
  viewBox="0 0 {size} {size / 2 + 2}"
  class={className}
  {...rest}
>
  <polygon
    points={trianglePoints("up", radius, size / 2, size / 2, 0)}
    fill={color}
  />
</svg>
