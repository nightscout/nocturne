import { type VariantProps, tv } from "tailwind-variants";

export const badgeVariants = tv({
  base: "focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive inline-flex w-fit shrink-0 items-center justify-center gap-1 overflow-hidden whitespace-nowrap rounded-md border px-2 py-0.5 text-xs font-medium transition-[color,box-shadow] focus-visible:ring-[3px] [&>svg]:pointer-events-none [&>svg]:size-3",
  variants: {
    variant: {
      default:
        "bg-primary text-primary-foreground [a&]:hover:bg-primary/90 border-transparent",
      secondary:
        "bg-secondary text-secondary-foreground [a&]:hover:bg-secondary/90 border-transparent",
      destructive:
        "bg-destructive [a&]:hover:bg-destructive/90 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40 dark:bg-destructive/70 border-transparent text-white",
      outline:
        "text-foreground [a&]:hover:bg-accent [a&]:hover:text-accent-foreground",
      success:
        "bg-success/15 text-success [a&]:hover:bg-success/25 border-transparent",
      warning:
        "bg-warning/15 text-warning [a&]:hover:bg-warning/25 border-transparent",
      info: "bg-info/15 text-info [a&]:hover:bg-info/25 border-transparent",
      // Marks synthetic demo data wherever it appears beside real data.
      demo: "bg-demo/15 text-demo [a&]:hover:bg-demo/25 border-transparent",
      // Notification and tracker urgency, as a solid count or level chip.
      "severity-urgent":
        "bg-severity-urgent text-severity-urgent-foreground [a&]:hover:bg-severity-urgent/90 border-transparent",
      "severity-hazard":
        "bg-severity-hazard text-severity-hazard-foreground [a&]:hover:bg-severity-hazard/90 border-transparent",
      "severity-warn":
        "bg-severity-warn text-severity-warn-foreground [a&]:hover:bg-severity-warn/90 border-transparent",
      "severity-info":
        "bg-severity-info text-severity-info-foreground [a&]:hover:bg-severity-info/90 border-transparent",
      // A record's category (ENTRY_CATEGORIES, TREATMENT_CATEGORIES); notes take secondary.
      "entry-bolus":
        "bg-entry-bolus/10 text-entry-bolus border-entry-bolus/30 [a&]:hover:bg-entry-bolus/20",
      "entry-basal":
        "bg-entry-basal/10 text-entry-basal border-entry-basal/30 [a&]:hover:bg-entry-basal/20",
      "entry-basal-injection":
        "bg-entry-basal-injection/10 text-entry-basal-injection border-entry-basal-injection/30 [a&]:hover:bg-entry-basal-injection/20",
      "entry-carbs":
        "bg-entry-carbs/10 text-entry-carbs border-entry-carbs/30 [a&]:hover:bg-entry-carbs/20",
      "entry-bg-check":
        "bg-entry-bg-check/10 text-entry-bg-check border-entry-bg-check/30 [a&]:hover:bg-entry-bg-check/20",
      "entry-device-event":
        "bg-entry-device-event/10 text-entry-device-event border-entry-device-event/30 [a&]:hover:bg-entry-device-event/20",
    },
    size: {
      default: "",
      // A count or tag packed into a dense row, a tab or a table header.
      sm: "px-1.5 py-0 text-2xs",
    },
    // Set by Badge from onremove; its remove button's focus ring shows on the badge,
    // since the badge clips overflow.
    removable: {
      true: "has-[[data-slot=badge-remove]:focus-visible]:border-ring has-[[data-slot=badge-remove]:focus-visible]:ring-[3px] has-[[data-slot=badge-remove]:focus-visible]:ring-ring/50",
      false: "",
    },
  },
  defaultVariants: {
    variant: "default",
    size: "default",
    removable: false,
  },
});

export type BadgeVariant = VariantProps<typeof badgeVariants>["variant"];
export type BadgeSize = VariantProps<typeof badgeVariants>["size"];

export { default as Badge } from "./badge.svelte";
