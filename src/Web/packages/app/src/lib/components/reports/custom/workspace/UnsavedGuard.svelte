<!--
  Asks before the page leaves a report with unsaved changes. A navigation in the app waits for the
  member's answer in a dialog. A reload, a closed tab or another site gets the browser's own question:
  SvelteKit cancels its "leave" navigation from the page's beforeunload event.
-->
<script lang="ts">
  import { beforeNavigate, goto } from "$app/navigation";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";

  interface Props {
    /** Whether the report has changes that are not saved. */
    dirty: boolean;
    /** Whether this browser keeps those changes (see `ReportDraft.kept`). */
    kept: boolean;
  }

  let { dirty, kept }: Props = $props();

  /** Where the cancelled navigation went, while the dialog asks. */
  let pending = $state<URL | null>(null);
  /** Set while the navigation the member confirmed runs, so it is not asked about again. */
  let leaving = false;

  beforeNavigate((navigation) => {
    if (!dirty || leaving) return;
    if (navigation.type === "leave") {
      navigation.cancel();
      return;
    }
    const to = navigation.to?.url;
    if (!to) return;
    navigation.cancel();
    pending = to;
  });

  async function leave() {
    const to = pending;
    pending = null;
    if (!to) return;
    leaving = true;
    try {
      // eslint-disable-next-line svelte/no-navigation-without-resolve -- the URL of a navigation SvelteKit already resolved
      if (to.origin === window.location.origin) await goto(to);
      else window.location.assign(to);
    } finally {
      leaving = false;
    }
  }
</script>

<ConfirmDialog
  open={pending !== null}
  onOpenChange={(open) => {
    if (!open) pending = null;
  }}
  title="Leave without saving?"
  confirmLabel="Leave"
  cancelLabel="Stay"
  destructive
  onConfirm={leave}
>
  {#snippet description()}
    {#if kept}
      Your changes are not saved. This browser keeps them, and the report opens with them when you come back.
    {:else}
      Your changes are not saved. This browser could not keep them, so you lose them when you leave.
    {/if}
  {/snippet}
</ConfirmDialog>
