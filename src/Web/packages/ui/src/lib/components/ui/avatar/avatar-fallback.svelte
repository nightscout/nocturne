<script lang="ts">
  import { Avatar as AvatarPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    ...restProps
  }: AvatarPrimitive.FallbackProps & {
    /** primary: a person's initials, tinted with the primary colour. */
    variant?: "default" | "primary";
  } = $props();
</script>

<AvatarPrimitive.Fallback
  bind:ref
  data-slot="avatar-fallback"
  class={cn(
    "bg-muted flex size-full items-center justify-center rounded-full",
    variant === "primary" && "bg-primary/10 text-primary",
    className
  )}
  {...restProps}
/>
