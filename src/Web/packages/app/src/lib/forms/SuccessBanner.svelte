<script lang="ts">
  import type { Snippet } from "svelte";
  import { Artwork, ConfirmationBackground, type IconArtworkSource } from "@nocturne/watercolour";
  import { cn } from "$lib/utils";

  /**
   * Confirms that something the user did has taken effect. The wash plays once
   * as it appears; without a GPU it is the baked still, and the banner's own
   * success colours carry it when there is no paint at all. `icon` paints the
   * thing that was added beside the message.
   */
  let {
    icon,
    class: className,
    children,
  }: { icon?: IconArtworkSource; class?: string; children: Snippet } = $props();
</script>

<div
  role="status"
  data-testid="success-banner"
  class={cn(
    "relative isolate flex items-center gap-3 overflow-hidden rounded-md border border-success/30 bg-success/10 p-3 text-sm text-success",
    className,
  )}
>
  <ConfirmationBackground palette="moss" motion="auto" fit="fill" class="-z-10" />
  {#if icon}
    <Artwork {icon} motion="auto" autoplay="once" class="-my-2 size-10 shrink-0" />
  {/if}
  <p>{@render children()}</p>
</div>
