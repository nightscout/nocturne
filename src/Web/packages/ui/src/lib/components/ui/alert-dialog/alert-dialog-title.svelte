<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const alertDialogTitleVariants = tv({
    base: "text-lg font-semibold",
    variants: {
      variant: {
        default: "",
        // Confirms an action that cannot be undone.
        destructive: "text-destructive",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  });

  export type AlertDialogTitleVariant = VariantProps<typeof alertDialogTitleVariants>["variant"];
</script>

<script lang="ts">
  import { AlertDialog as AlertDialogPrimitive } from "bits-ui";
  import { cn } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    ...restProps
  }: AlertDialogPrimitive.TitleProps & { variant?: AlertDialogTitleVariant } = $props();
</script>

<AlertDialogPrimitive.Title
  bind:ref
  data-slot="alert-dialog-title"
  class={cn(alertDialogTitleVariants({ variant }), className)}
  {...restProps}
/>
