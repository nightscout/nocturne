import { type VariantProps, tv } from "tailwind-variants";

// Shared by PopoverTrigger and TooltipTrigger, which otherwise render a bare button.
export const triggerVariants = tv({
  base: "",
  variants: {
    variant: {
      default: "",
      // A term in running text that opens its definition or a breakdown.
      term: "inline-flex cursor-help items-center gap-1 underline decoration-muted-foreground/50 decoration-dotted underline-offset-2",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type TriggerVariant = VariantProps<typeof triggerVariants>["variant"];
