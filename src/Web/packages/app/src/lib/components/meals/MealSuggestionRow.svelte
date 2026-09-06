<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Sparkles } from "lucide-svelte";
  import type { SuggestedMealMatch } from "$lib/api";

  let {
    suggestion,
    onAccept,
    onDismiss,
    onReview,
  }: {
    suggestion: SuggestedMealMatch;
    onAccept: (suggestion: SuggestedMealMatch) => Promise<void> | void;
    onDismiss: (suggestion: SuggestedMealMatch) => Promise<void> | void;
    onReview: (suggestion: SuggestedMealMatch) => void;
  } = $props();

  // Accepting or dismissing removes the row, but only once the refreshed
  // suggestions come back. Until then a second click would send the command
  // again, attributing the same food entry twice.
  let pending = $state<"accept" | "dismiss" | null>(null);

  async function run(action: "accept" | "dismiss", e: MouseEvent) {
    e.stopPropagation();
    if (pending) return;
    pending = action;
    try {
      await (action === "accept" ? onAccept(suggestion) : onDismiss(suggestion));
    } finally {
      pending = null;
    }
  }
</script>

<div class="flex items-center justify-between gap-4">
  <div class="flex items-center gap-3 min-w-0">
    <Sparkles class="h-4 w-4 text-primary shrink-0" />
    <div class="min-w-0">
      <span class="font-medium truncate">
        {suggestion.foodName ?? suggestion.mealName ?? "Food entry"}
      </span>
      <span class="text-sm text-muted-foreground ml-2">
        {suggestion.carbs}g carbs
        · {Math.round((suggestion.matchScore ?? 0) * 100)}% match
      </span>
    </div>
  </div>
  <div class="flex items-center gap-2 shrink-0">
    <Button
      type="button"
      variant="ghost"
      size="sm"
      disabled={pending !== null}
      onclick={(e: MouseEvent) => run("dismiss", e)}
    >
      {pending === "dismiss" ? "Dismissing…" : "Dismiss"}
    </Button>
    <Button
      type="button"
      variant="outline"
      size="sm"
      disabled={pending !== null}
      onclick={(e: MouseEvent) => {
        e.stopPropagation();
        onReview(suggestion);
      }}
    >
      Review
    </Button>
    <Button
      type="button"
      size="sm"
      disabled={pending !== null}
      onclick={(e: MouseEvent) => run("accept", e)}
    >
      {pending === "accept" ? "Accepting…" : "Accept"}
    </Button>
  </div>
</div>
