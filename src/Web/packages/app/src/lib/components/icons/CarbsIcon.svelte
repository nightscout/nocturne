<script lang="ts">
  /**
   * Carbs hemisphere icon - bowl shape curving below baseline. Used for carb
   * treatment markers on charts and in legends. Complements BolusIcon to form a
   * complete circle when both are present.
   */
  import type { IconProps } from "./types";

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
  <!-- Hemisphere (bowl shape - curves below baseline) -->
  <path
    d="M {size / 2 - radius},{0} A {radius},{radius} 0 0,1 {size / 2 +
      radius},{0} Z"
    fill={color}
  />
</svg>
