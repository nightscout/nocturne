<script lang="ts">
  /**
   * Carbs marker icon — the upper half of the chart's meal diamond, drawn to
   * fill its own icon box rather than sharing the chart's baseline. Used for
   * carb treatment markers in legends and stat cards; the chart draws the same
   * shape from <see>marker-shapes</see>.
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
