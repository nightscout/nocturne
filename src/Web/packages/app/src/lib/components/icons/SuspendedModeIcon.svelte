<script lang="ts">
  import PauseCircle from "lucide-svelte/icons/pause-circle";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Suspended mode - insulin delivery paused -->
<PauseCircle class={className} {...rest} />
