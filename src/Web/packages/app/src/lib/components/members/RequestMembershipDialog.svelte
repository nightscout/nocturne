<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import { browser } from "$app/env";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { parkMembershipRequest } from "#lib/membership-request-storage.js";

  interface Props {
    open: boolean;
    /** Where sign-in should land. Defaults to the current page. */
    returnUrl?: string;
  }

  let { open = $bindable(false), returnUrl }: Props = $props();

  let message = $state("");

  function handleSubmit() {
    if (!browser) return;

    const returnPath = parkMembershipRequest(
      sessionStorage,
      page.url.host,
      message,
      returnUrl ?? page.url.pathname + page.url.search,
      crypto.randomUUID(),
      Date.now(),
    );

    open = false;

    goto(resolve(`auth/login?${new URLSearchParams({ returnUrl: returnPath })}`));
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md" data-testid="request-membership-dialog">
    <Dialog.Header>
      <Dialog.Title>Request membership</Dialog.Title>
      <Dialog.Description>
        Introduce yourself to the site owner so they know who you are.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <Textarea
        bind:value={message}
        placeholder="e.g. I'm Sarah's endocrinologist"
        maxlength={500}
        rows={3}
      />
      <p class="text-xs text-muted-foreground text-right">
        {message.length}/500
      </p>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => open = false}>Cancel</Button>
      <Button onclick={handleSubmit}>Continue to sign up</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
