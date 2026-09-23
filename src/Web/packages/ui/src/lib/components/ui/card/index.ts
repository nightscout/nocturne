import Root from "./card.svelte";
import Content from "./card-content.svelte";
import Description from "./card-description.svelte";
import Footer from "./card-footer.svelte";
import Header from "./card-header.svelte";
import Title from "./card-title.svelte";
import Action from "./card-action.svelte";
import { type VariantProps, tv } from "tailwind-variants";

export const cardVariants = tv({
  base: "bg-card text-card-foreground flex flex-col gap-6 rounded-xl border py-6 shadow-sm",
  variants: {
    variant: {
      default: "",
      destructive: "border-destructive/50 bg-destructive/5",
      success: "border-success/30 bg-success/5",
      warning: "border-warning/30 bg-warning/5",
      info: "border-info/30 bg-info/5",
    },
  },
  defaultVariants: {
    variant: "default",
  },
});

export type CardVariant = VariantProps<typeof cardVariants>["variant"];

export {
	Root,
	Content,
	Description,
	Footer,
	Header,
	Title,
	Action,
	//
	Root as Card,
	Content as CardContent,
	Description as CardDescription,
	Footer as CardFooter,
	Header as CardHeader,
	Title as CardTitle,
	Action as CardAction,
};
