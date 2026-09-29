<script lang="ts">
  import type { Snippet } from "svelte";
  import { ConfirmationBackground } from "@nocturne/watercolour";
  import { cn } from "$lib/utils";

  /**
   * Confirms that something the user did has taken effect. The wash plays once
   * as it appears; without a GPU it is the baked still, and the banner's own
   * success colours carry it when there is no paint at all.
   */
  let {
    wash = true,
    class: className,
    children,
  }: {
    /** False for a removal, revocation or denial: those are not celebrated with paint. */
    wash?: boolean;
    class?: string;
    children: Snippet;
  } = $props();
</script>

<div
  role="status"
  data-testid="success-banner"
  class={cn(
    "relative isolate overflow-hidden rounded-md border border-success/30 bg-success/10 p-3 text-sm text-success",
    className,
  )}
>
  {#if wash}
    <ConfirmationBackground palette="moss" motion="auto" fit="fill" class="-z-10" />
  {/if}
  <p>{@render children()}</p>
</div>
