import { type VariantProps, tv } from "tailwind-variants";

// Sizes mirror SelectTrigger's so a row of mixed controls shares one height.
export const inputVariants = tv({
  base: [
    "border-input selection:bg-primary dark:bg-input/30 selection:text-primary-foreground ring-offset-background placeholder:text-muted-foreground shadow-xs flex w-full min-w-0 rounded-md border outline-none transition-[color,box-shadow] disabled:cursor-not-allowed disabled:opacity-50",
    "focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]",
    "aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
  ],
  variants: {
    size: {
      default: "h-9 px-3 text-base md:text-sm",
      sm: "h-8 px-3 text-sm",
      xs: "h-7 px-2 text-xs",
    },
    variant: {
      default: "",
      // Device and pairing codes the user reads off another screen.
      code: "text-center text-lg uppercase tracking-widest md:text-lg",
    },
  },
  defaultVariants: {
    size: "default",
    variant: "default",
  },
});

export type InputSize = VariantProps<typeof inputVariants>["size"];
export type InputVariant = VariantProps<typeof inputVariants>["variant"];
