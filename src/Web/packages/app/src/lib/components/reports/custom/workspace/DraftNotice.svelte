<!--
  Says that the report opened with the unsaved changes this browser kept, with an action that
  discards them.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import Undo2 from "@lucide/svelte/icons/undo-2";

  interface Props {
    ondiscard: () => void;
    /** What discarding returns to. */
    discardLabel?: string;
  }

  let { ondiscard, discardLabel = "Discard changes" }: Props = $props();
</script>

<div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm" role="status" data-testid="draft-restored">
  <p class="text-muted-foreground">Restored your unsaved changes.</p>
  <Button variant="outline" size="touch-sm" onclick={ondiscard}>
    <Undo2 aria-hidden="true" />
    {discardLabel}
  </Button>
</div>
