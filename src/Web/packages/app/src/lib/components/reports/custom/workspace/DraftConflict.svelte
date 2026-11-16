<!--
  Asks what to do with the unsaved changes this browser kept for a report that was saved again
  somewhere else after the changes were made (see `ReportDraft.stale`).
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import Check from "@lucide/svelte/icons/check";
  import Undo2 from "@lucide/svelte/icons/undo-2";

  interface Props {
    onkeep: () => void;
    ondiscard: () => void;
  }

  let { onkeep, ondiscard }: Props = $props();
</script>

<div class="space-y-3 rounded-lg border border-warning/40 bg-warning/5 p-4" role="alert" data-testid="draft-conflict">
  <p class="text-sm font-medium">This report changed somewhere else after you made your unsaved changes.</p>
  <p class="text-sm text-muted-foreground">
    If you keep your changes and save them, they replace the other changes. If you discard your changes, the
    report opens as it is now.
  </p>
  <div class="flex flex-wrap gap-2">
    <Button variant="outline" size="touch-sm" onclick={onkeep}>
      <Check aria-hidden="true" />
      Keep my changes
    </Button>
    <Button variant="outline" size="touch-sm" onclick={ondiscard}>
      <Undo2 aria-hidden="true" />
      Discard my changes
    </Button>
  </div>
</div>
