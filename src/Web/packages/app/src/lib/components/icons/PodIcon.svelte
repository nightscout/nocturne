<script lang="ts">
  import type { IconProps } from "./types";

  let { size = 24, color = "currentColor", class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Pod tracker icon: a patch pump body with its insulin window and cannula -->
<svg width={size} height={size} viewBox="0 0 24 24" fill="none" class={className} {...rest}>
  <rect x="2.5" y="6" width="19" height="12" rx="6" stroke={color} stroke-width="1.75" />
  <rect x="6.5" y="9.5" width="6.5" height="5" rx="1.5" stroke={color} stroke-width="1.5" />
  <circle cx="16.75" cy="12" r="1.5" fill={color} />
</svg>
