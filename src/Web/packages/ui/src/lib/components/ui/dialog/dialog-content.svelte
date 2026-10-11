<script lang="ts">
  import { Dialog as DialogPrimitive } from "bits-ui";
  import X from "@lucide/svelte/icons/x";
  import type { Snippet } from "svelte";
  import DialogOverlay from "./dialog-overlay.svelte";
  import { cn, type WithoutChildrenOrChild } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    portalProps,
    children,
    showClose = true,
    layout = "default",
    ...restProps
  }: WithoutChildrenOrChild<DialogPrimitive.ContentProps> & {
    portalProps?: DialogPrimitive.PortalProps;
    showClose?: boolean;
    layout?: "default" | "workspace";
    children: Snippet;
  } = $props();
</script>

<DialogPrimitive.Portal {...portalProps}>
  <DialogOverlay />
  <DialogPrimitive.Content
    bind:ref
    data-slot="dialog-content"
    class={cn(
      "bg-background data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 fixed left-[50%] top-[50%] z-50 w-full translate-x-[-50%] translate-y-[-50%] rounded-lg border shadow-lg duration-200",
      layout === "workspace"
        ? "flex h-[90dvh] w-[calc(100%-1rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0"
        : "grid max-w-[calc(100%-2rem)] gap-4 p-6 sm:max-w-lg",
      className,
    )}
    {...restProps}
  >
    {@render children?.()}
    {#if showClose}
      <DialogPrimitive.Close
        class="ring-offset-background focus:ring-ring rounded-xs focus:outline-hidden absolute right-4 top-4 opacity-70 transition-opacity hover:opacity-100 focus:ring-2 focus:ring-offset-2 disabled:pointer-events-none [&_svg:not([class*='size-'])]:size-4 [&_svg]:pointer-events-none [&_svg]:shrink-0"
      >
        <X />
        <span class="sr-only">Close</span>
      </DialogPrimitive.Close>
    {/if}
  </DialogPrimitive.Content>
</DialogPrimitive.Portal>
