<script lang="ts">
  import { untrack } from "svelte";
  import type { TrackerCategory, TrackerDefinitionDto } from "#lib/api/index.js";
  import * as Tooltip from "#lib/components/ui/tooltip/index.js";
  import { draftFrom } from "./draft";
  import TrackerEditorDialog from "./TrackerEditorDialog.svelte";

  interface Props {
    open: boolean;
    definition: TrackerDefinitionDto;
    categoryLabels: Record<TrackerCategory, string>;
  }

  let { open, definition, categoryLabels }: Props = $props();

  // As the trackers page does: the opener builds the draft the dialog edits.
  let draft = $state(untrack(() => draftFrom(definition)));
</script>

<Tooltip.Provider>
  <TrackerEditorDialog {open} {definition} bind:draft {categoryLabels} />
</Tooltip.Provider>
