<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";

  interface Props {
    open: boolean;
    tenantSlug?: string;
  }

  let { open = $bindable(false), tenantSlug }: Props = $props();

  let message = $state("");

  const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

  function handleSubmit() {
    if (!browser || !tenantSlug) return;

    try {
      localStorage.setItem(`${STORAGE_KEY_PREFIX}${tenantSlug}`, message);
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
