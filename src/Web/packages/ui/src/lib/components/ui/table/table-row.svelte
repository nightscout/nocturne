<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const tableRowVariants = tv({
    // data-state="open" marks a row whose detail row is expanded beneath it.
    base: "hover:bg-muted/50 data-[state=selected]:bg-muted data-[state=open]:bg-muted/50 border-b transition-colors",
    variants: {
      variant: {
        default: "",
        // Heads a group of rows, e.g. a day's meals.
        group: "bg-muted/50 hover:bg-muted/60",
        // The expanded detail beneath a row; it is not itself interactive.
        detail: "bg-muted/30 hover:bg-muted/30",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  });

  export type TableRowVariant = VariantProps<typeof tableRowVariants>["variant"];
</script>

<script lang="ts">
  import { cn, type WithElementRef } from "../../../utils";
  import type { HTMLAttributes } from "svelte/elements";

  let {
    ref = $bindable(null),
    class: className,
    variant = "default",
    children,
    ...restProps
  }: WithElementRef<HTMLAttributes<HTMLTableRowElement>> & {
    variant?: TableRowVariant;
  } = $props();
</script>

<tr
  bind:this={ref}
  data-slot="table-row"
  class={cn(tableRowVariants({ variant }), className)}
  {...restProps}
>
  {@render children?.()}
</tr>
