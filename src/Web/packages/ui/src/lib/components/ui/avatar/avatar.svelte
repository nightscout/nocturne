<script lang="ts">
  import { Avatar as AvatarPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    size,
    ...restProps
  }: AvatarPrimitive.RootProps & {
    /** Box and initials size: sm 32px, md 40px, lg 64px. Unset keeps the 32px box and inherited text. */
    size?: "sm" | "md" | "lg";
  } = $props();
</script>

<AvatarPrimitive.Root
  bind:ref
  data-slot="avatar"
  class={cn(
    "relative flex size-8 shrink-0 overflow-hidden rounded-full",
    size === "sm" && "text-xs",
    size === "md" && "size-10 text-sm",
    size === "lg" && "size-16 text-xl",
    className
  )}
  {...restProps}
/>
