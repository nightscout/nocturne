import Root from "./button.svelte";
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
      // A quiet secondary action.
      "ghost-muted":
        "text-muted-foreground hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
      // Muted text with no fill: a disclosure toggle, or a secondary action in a line of text.
      subtle: "text-muted-foreground hover:text-foreground",
      // A quiet remove action.
      "ghost-destructive":
        "text-destructive hover:bg-destructive/10 hover:text-destructive dark:hover:bg-destructive/20",
      // A bordered remove action.
      "outline-destructive":
        "bg-background text-destructive shadow-xs hover:bg-destructive/10 dark:bg-input/30 dark:hover:bg-destructive/20 border border-destructive/30",
      // The trigger of a combobox or date picker, typed like SelectTrigger.
      combobox:
        "bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:bg-input/30 dark:border-input dark:hover:bg-input/50 border font-normal",
      // The add-item placeholder at the end of a list.
      dashed:
        "text-muted-foreground hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50 border border-dashed",
      // An option in a menu that is not a DropdownMenu, e.g. an editor's bubble or slash menu;
      // data-highlighted marks the keyboard-selected one. Pair with size="menu".
      menu: "hover:bg-accent hover:text-accent-foreground data-highlighted:bg-accent data-highlighted:text-accent-foreground [&_svg:not([class*='text-'])]:text-muted-foreground",
      // Over a full-screen display whose background the theme does not own, e.g. a clock face.
      overlay: "text-white/80 hover:bg-white/10 hover:text-white",
      // A colour the theme does not own, e.g. an admin-configured sign-in provider. Pass
      // it as `brand`; the foreground has to be chosen for contrast by whoever owns the colour.
      brand:
        "bg-(--button-bg) text-(--button-fg) border border-(--button-bg) shadow-xs hover:opacity-90",
    },
    size: {
      default: "h-9 px-4 py-2 has-[>svg]:px-3",
      xs: "h-7 gap-1 rounded-md px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
      sm: "h-8 gap-1.5 rounded-md px-3 has-[>svg]:px-2.5",
      lg: "h-10 rounded-md px-6 has-[>svg]:px-4",
      // A marketing call to action: lg, set in body-size type.
      cta: "h-10 rounded-md px-6 text-base has-[>svg]:px-4",
      // Full-screen alarm and emergency actions: a touch target read at a glance.
      xl: "h-14 rounded-md px-8 text-lg font-semibold shadow-lg has-[>svg]:px-6 [&_svg:not([class*='size-'])]:size-5",
      icon: "size-9",
      "icon-xs": "size-7 rounded-md [&_svg:not([class*='size-'])]:size-3",
      "icon-sm": "size-8",
      // A round remove pip pinned to the corner of a thumbnail or tile, or a remove in a dense row.
      "icon-2xs": "size-5 rounded-full [&_svg:not([class*='size-'])]:size-3",
      // Matches DropdownMenu.Item: a full-width, left-aligned row in a menu. A remove is variant="ghost-destructive".
      menu: "h-8 w-full justify-start gap-2 rounded-sm px-2 py-1.5 font-normal",
      // A link-style button that sits in running text or a label row.
      inline: "h-auto gap-1 p-0",
      // The same, beside a caption or other text-xs copy.
      "inline-xs": "h-auto gap-1 p-0 text-xs [&_svg:not([class*='size-'])]:size-3",
    },
    // Hidden until the enclosing `group` is hovered or holds focus; keyboard focus always shows it.
    reveal: {
      true: "opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100",
      false: "",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
    reveal: false,
  },
});

export type ButtonVariant = VariantProps<typeof buttonVariants>["variant"];
export type ButtonSize = VariantProps<typeof buttonVariants>["size"];

export type ButtonProps = WithElementRef<HTMLButtonAttributes> &
  WithElementRef<HTMLAnchorAttributes> & {
    variant?: ButtonVariant;
    size?: ButtonSize;
    reveal?: boolean;
    /** Fills variant="brand" (as --button-bg and --button-fg). */
    brand?: { background: string; foreground: string };
  };

export {
	Root,
	type ButtonProps as Props,
	//
	Root as Button,
};
