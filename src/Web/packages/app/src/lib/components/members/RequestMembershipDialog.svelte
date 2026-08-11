<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { membershipRequestStorageKey } from "$lib/membership-request-storage";

  interface Props {
    open: boolean;
  }

  let { open = $bindable(false) }: Props = $props();

  let message = $state("");

  function handleSubmit() {
    if (!browser) return;

    try {
      localStorage.setItem(membershipRequestStorageKey(window.location.host), message);
    } catch {
      // Storage full or unavailable
    }

    open = false;

    const returnUrl = encodeURIComponent(window.location.pathname);
    goto(`/auth/login?returnUrl=${returnUrl}`);
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Request Membership</Dialog.Title>
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
      <Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
      <Button onclick={handleSubmit}>Continue to Sign Up</Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
