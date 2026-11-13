import Root from "./input.svelte";
import { type VariantProps, tv } from "tailwind-variants";

// Sizes mirror SelectTrigger's so a row of mixed controls shares one height.
export const inputVariants = tv({
  base: [
    "border-input selection:bg-primary dark:bg-input/30 selection:text-primary-foreground ring-offset-background placeholder:text-muted-foreground shadow-xs flex w-full min-w-0 rounded-md border outline-none transition-[color,box-shadow] disabled:cursor-not-allowed disabled:opacity-50",
    "focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]",
    "aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive",
    // A value the server has accepted, e.g. an available username.
    "data-valid:not-aria-invalid:border-success/60",
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
      // A name edited in place, e.g. a clock face's or a comparison period's; its border shows on focus.
      title: "border-transparent bg-transparent font-semibold shadow-none dark:bg-transparent",
      // A run of text inside a larger field that owns the border and the focus ring, e.g. the
      // subdomain typed into a site address. It takes the size of the text around it.
      inline:
        "h-auto w-auto rounded-none border-0 border-b-2 border-input bg-transparent p-0 text-center text-[length:inherit] font-medium shadow-none focus-visible:border-primary focus-visible:ring-0 dark:bg-transparent md:text-[length:inherit]",
    },
  },
  defaultVariants: {
    size: "default",
    variant: "default",
  },
});

export type InputSize = VariantProps<typeof inputVariants>["size"];
export type InputVariant = VariantProps<typeof inputVariants>["variant"];

export {
	Root,
	//
	Root as Input,
};
