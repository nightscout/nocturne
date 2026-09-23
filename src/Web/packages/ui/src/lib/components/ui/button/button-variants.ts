import type { WithElementRef } from "../../../utils";
import type {
  HTMLAnchorAttributes,
  HTMLButtonAttributes,
} from "svelte/elements";
import { type VariantProps, tv } from "tailwind-variants";

export const buttonVariants = tv({
  base: "focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive inline-flex shrink-0 items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium outline-none transition-all focus-visible:ring-[3px] disabled:pointer-events-none disabled:opacity-50 aria-disabled:pointer-events-none aria-disabled:opacity-50 [&_svg:not([class*='size-'])]:size-4 [&_svg]:pointer-events-none [&_svg]:shrink-0",
  variants: {
    variant: {
      default:
        "bg-primary text-primary-foreground shadow-xs hover:bg-primary/90",
      destructive:
        "bg-destructive shadow-xs hover:bg-destructive/90 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40 dark:bg-destructive/60 text-white",
      outline:
        "bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:bg-input/30 dark:border-input dark:hover:bg-input/50 border",
      secondary:
        "bg-secondary text-secondary-foreground shadow-xs hover:bg-secondary/80",
      ghost:
        "hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
      link: "text-primary underline-offset-4 hover:underline",
      "ghost-muted":
        "text-muted-foreground hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
      "ghost-destructive":
        "text-destructive hover:bg-destructive/10 hover:text-destructive dark:hover:bg-destructive/20",
      "outline-destructive":
        "bg-background text-destructive shadow-xs hover:bg-destructive/10 dark:bg-input/30 dark:hover:bg-destructive/20 border border-destructive/30",
      dashed:
        "text-muted-foreground hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50 border border-dashed",
    },
    size: {
      default: "h-9 px-4 py-2 has-[>svg]:px-3",
      xs: "h-7 gap-1 rounded-md px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
      sm: "h-8 gap-1.5 rounded-md px-3 has-[>svg]:px-2.5",
      lg: "h-10 rounded-md px-6 has-[>svg]:px-4",
      // Full-screen alarm and emergency actions: a touch target read at a glance.
      xl: "h-14 rounded-md px-8 text-lg has-[>svg]:px-6 [&_svg:not([class*='size-'])]:size-5",
      icon: "size-9",
      "icon-xs": "size-7 rounded-md [&_svg:not([class*='size-'])]:size-3",
      "icon-sm": "size-8",
      // A link-style button that sits in running text or a label row.
      inline: "h-auto gap-1 p-0",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
  },
});

export type ButtonVariant = VariantProps<typeof buttonVariants>["variant"];
export type ButtonSize = VariantProps<typeof buttonVariants>["size"];

export type ButtonProps = WithElementRef<HTMLButtonAttributes> &
  WithElementRef<HTMLAnchorAttributes> & {
    variant?: ButtonVariant;
    size?: ButtonSize;
  };
