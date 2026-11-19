<script lang="ts">
  import type { IconProps } from "@nocturne/charts/core";

  let { size = 24, color = "currentColor", class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Unknown/question event icon (AAPS IcQuestion) -->
<svg width={size} height={size} viewBox="0 0 24 24" fill="none" class={className} {...rest}>
  <path fill={color} d="M12.062,17.861c-0.58,0-1.05-0.47-1.05-1.051c0-3.776,1.76-5.094,3.175-6.153c1.185-0.888,2.042-1.529,2.042-3.605c0-2.862-2.749-3.752-3.75-3.752c-2.157,0-3.9,1.293-4.78,3.547C7.487,7.388,6.876,7.656,6.338,7.444c-0.541-0.211-0.807-0.82-0.596-1.361C6.936,3.025,9.454,1.2,12.479,1.2c2.359,0,5.852,1.86,5.852,5.852c0,3.127-1.599,4.324-2.885,5.286c-1.304,0.976-2.333,1.746-2.333,4.472C13.113,17.39,12.642,17.861,12.062,17.861z"/>
  <circle fill={color} cx="12.124" cy="21.349" r="1.451"/>
</svg>
