<script lang="ts">
  import type { HTMLAnchorAttributes } from "svelte/elements";
  import { X } from "@lucide/svelte";
  import { cn, type WithElementRef } from "../../../utils";
  import { badgeVariants, type BadgeSize, type BadgeVariant } from "./index.js";

  // A removable badge must name what its remove button removes.
  type Removal =
    | { onremove?: () => void; removeLabel: string }
    | { onremove?: undefined; removeLabel?: undefined };

  let {
    ref = $bindable(null),
    href,
    class: className,
    variant = "default",
    size = "default",
    live = false,
    onremove,
    removeLabel,
    children,
    ...restProps
  }: WithElementRef<HTMLAnchorAttributes> &
    Removal & {
      variant?: BadgeVariant;
      size?: BadgeSize;
      live?: boolean;
    } = $props();
</script>

<svelte:element
  this={href ? "a" : "span"}
  bind:this={ref}
  data-slot="badge"
  {href}
  class={cn(badgeVariants({ variant, size, live, removable: onremove !== undefined }), className)}
  {...restProps}
>
  {@render children?.()}
  {#if onremove}
    <button
      type="button"
      data-slot="badge-remove"
      class="-mr-0.5 inline-flex shrink-0 items-center rounded-sm opacity-70 outline-none transition-opacity hover:opacity-100 focus-visible:opacity-100"
      aria-label={removeLabel}
      onclick={onremove}
    >
      <X class="size-3" />
    </button>
  {/if}
</svelte:element>
