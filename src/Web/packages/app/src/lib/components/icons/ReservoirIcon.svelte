<script lang="ts">
  import Droplet from "lucide-svelte/icons/droplet";
  import type { IconProps } from "./types";

  let { class: className = "", ...rest }: IconProps = $props();
</script>

<!-- Reservoir icon - insulin reservoir/cartridge change -->
<Droplet class={className} {...rest} />
