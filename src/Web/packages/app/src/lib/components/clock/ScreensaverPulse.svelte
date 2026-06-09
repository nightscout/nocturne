<script module lang="ts">
  /** Duration of the corner-hit pulse animation, in milliseconds. Must match the CSS keyframe below. */
  export const PULSE_DURATION_MS = 1200;
</script>

<script lang="ts">
  interface Props {
    /** Viewport coordinate where the corner hit occurred. */
    x: number;
    y: number;
  }

  let { x, y }: Props = $props();
</script>

<svg
  class="pointer-events-none fixed inset-0 z-30 h-full w-full"
  aria-hidden="true"
>
  <circle
    cx={x}
    cy={y}
    fill="none"
    stroke="white"
    stroke-width="2"
    class="pulse"
  />
</svg>

<style>
  .pulse {
    animation: screensaver-pulse 1200ms ease-out forwards;
  }
  @keyframes screensaver-pulse {
    from {
      r: 0;
      opacity: 0.6;
    }
    to {
      r: 30vmax;
      opacity: 0;
    }
  }
</style>
