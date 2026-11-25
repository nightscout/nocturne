<script lang="ts">
  import type { IconProps } from "@nocturne/charts/core";

  let { size = 24, color = "currentColor", class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Site rotation icon (AAPS IcSiteRotation) -->
<svg width={size} height={size} viewBox="0 0 24 24" fill="none" class={className} {...rest}>
  <path fill={color} d="M8.128,5.493L7.22,3.968C4.489,5.6,2.649,8.576,2.642,11.981H1.2l2.33,4.035l2.33-4.035h-1.44C4.426,9.224,5.916,6.815,8.128,5.493z M20.47,7.978l-2.33,4.035h1.44c-0.005,2.759-1.496,5.171-3.709,6.493l0.908,1.525c2.733-1.633,4.573-4.611,4.578-8.018H22.8L20.47,7.978z M11.981,19.581c-2.757-0.007-5.166-1.497-6.488-3.709L3.968,16.78c1.632,2.731,4.608,4.571,8.013,4.578V22.8l4.035-2.33l-4.035-2.33V19.581z"/>
  <path fill={color} opacity="0.7" d="M12.013,2.642V1.2L7.978,3.53l4.035,2.33v-1.44c2.76,0.005,5.171,1.496,6.493,3.709l1.525-0.908C18.398,4.487,15.421,2.647,12.013,2.642z"/>
</svg>
