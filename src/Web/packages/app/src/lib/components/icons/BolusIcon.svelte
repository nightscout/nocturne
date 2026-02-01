<script lang="ts">
  /**
   * Bolus hemisphere icon - dome shape curving above baseline. Used for bolus
   * treatment markers on charts and in legends. When isOverride is true,
   * displays a triangle instead to indicate manual override.
   */
  import type { IconProps } from "./types";

  interface BolusIconProps extends IconProps {
    /**
     * Whether this bolus was a manual override (shows triangle instead of
     * hemisphere)
     */
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
</script>

<svg
  width={size}
  height={size / 2 + 2}
  viewBox="0 0 {size} {size / 2 + 2}"
  class={className}
  {...rest}
>
  {#if isOverride}
    <!-- Triangle for manual override -->
    <polygon
      points="{size / 2},{size / 2} {size / 2 - radius},{0} {size / 2 +
        radius},{0}"
      fill={color}
    />
  {:else}
    <!-- Hemisphere (dome shape - curves above baseline) -->
    <path
      d="M {size / 2 - radius},{size / 2} A {radius},{radius} 0 0,0 {size / 2 +
        radius},{size / 2} Z"
      fill={color}
    />
  {/if}
</svg>
