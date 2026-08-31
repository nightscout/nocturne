import { describe, it, expect } from "vitest";
import { cn } from "$lib/utils";
import { buttonVariants } from "$lib/components/ui/button";

/**
 * What `AlertDialog.Action` renders: its own `buttonVariants()` merged with
 * whatever `class` the caller passed. ConfirmDialog passes the destructive
 * variant, so the assertions below are on the string the button ends up with.
 */
function actionClasses(destructive: boolean): string {
  return cn(
    buttonVariants(),
    destructive ? buttonVariants({ variant: "destructive" }) : undefined
  );
}

describe("ConfirmDialog confirm button", () => {
  it("keeps the default variant when the action is not destructive", () => {
    const classes = actionClasses(false);

    expect(classes).toContain("bg-primary");
    expect(classes).toContain("text-primary-foreground");
  });

  it("takes the whole destructive variant, foreground and all", () => {
    const classes = actionClasses(true);

    expect(classes).toContain("bg-destructive");
    expect(classes).toContain("hover:bg-destructive/90");
    // Dark-mode surface and focus ring only exist on the variant, so a
    // hand-written `bg-destructive` would silently drop them.
    expect(classes).toContain("dark:bg-destructive/60");
    expect(classes).toContain("focus-visible:ring-destructive/20");
  });

  it("names a foreground colour the stylesheet actually defines", () => {
    const classes = actionClasses(true);

    // The theme registers --color-destructive but no --color-destructive-foreground,
    // so `text-destructive-foreground` compiles to nothing while still winning the
    // tailwind-merge conflict against the default variant's foreground.
    expect(classes).not.toContain("text-destructive-foreground");
    expect(classes).toContain("text-white");
    expect(classes).not.toContain("text-primary-foreground");
  });
});
