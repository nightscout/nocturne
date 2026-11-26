<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import UserCheck from "@lucide/svelte/icons/user-check";

  interface Props {
    name: string;
    disabled: boolean;
    working: boolean;
    onConfirm: () => void;
  }

  let { name, disabled, working, onConfirm }: Props = $props();
</script>

<ConfirmDialog title={`Let ${name} back in?`} confirmLabel="Let back in" {onConfirm}>
  {#snippet trigger(props)}
    <Button {...props} variant="outline" size="sm" aria-label={`Let ${name} back in`} {disabled}>
      {#if working}
        <Loader2 class="mr-1.5 h-3.5 w-3.5 animate-spin" />
      {:else}
        <UserCheck class="mr-1.5 h-3.5 w-3.5" />
      {/if}
      Let back in
    </Button>
  {/snippet}
  {#snippet description()}
    {name} gets back what each team gives them at this site. If they have approval, or you trust the whole
    team, they see this site's data again. If a team limits its staff to their groups, they must also be in a group
    with this site. If they wait for approval and you do not trust the whole team, they wait again. If they have a decline and you do not trust the whole team, they stay out.
  {/snippet}
</ConfirmDialog>
