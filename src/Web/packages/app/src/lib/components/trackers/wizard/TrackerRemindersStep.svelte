<script lang="ts">
  import { TrackerMode } from "#lib/api/index.js";
  import { TrackerNotificationEditor } from "#lib/components/trackers/index.js";
  import type { TrackerDraft } from "../draft";
  import TrackerVisibilityField from "../TrackerVisibilityField.svelte";

  interface Props {
    /** Edited in place. */
    draft: TrackerDraft;
  }

  let { draft = $bindable() }: Props = $props();
</script>

<div class="space-y-8">
  <TrackerNotificationEditor
    bind:notifications={draft.notifications}
    mode={draft.mode}
    lifespanHours={draft.mode === TrackerMode.Duration ? draft.lifespanHours : undefined}
  />

  <div class="max-w-sm">
    <TrackerVisibilityField bind:draft />
  </div>
</div>
