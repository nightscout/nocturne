<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { invalidateAll } from "$app/navigation";
  import { page } from "$app/state";
  import LoginForm from "./LoginForm.svelte";
  import { confirmIdentity } from "./confirm-identity.svelte";

  const returnUrl = $derived(page.url.pathname + page.url.search);
  let signingIn = $state(false);

  async function signedIn() {
    await invalidateAll();
    confirmIdentity.settle(true);
  }
</script>

<Dialog.Root
  open={confirmIdentity.open}
  onOpenChange={(open) => {
    if (!open) confirmIdentity.settle(false);
  }}
>
  <Dialog.Content
    class="sm:max-w-md"
    showClose={!signingIn}
    escapeKeydownBehavior={signingIn ? "ignore" : "close"}
    interactOutsideBehavior={signingIn ? "ignore" : "close"}
  >
    <Dialog.Header>
      <Dialog.Title>Confirm it is you</Dialog.Title>
      <Dialog.Description>
        This session started from a sign-in link. Sign in again, then do the action again.
      </Dialog.Description>
    </Dialog.Header>

    {#if confirmIdentity.open}
      <LoginForm
        {returnUrl}
        onSuccess={signedIn}
        onBusyChange={(busy) => (signingIn = busy)}
      />
    {/if}
  </Dialog.Content>
</Dialog.Root>
