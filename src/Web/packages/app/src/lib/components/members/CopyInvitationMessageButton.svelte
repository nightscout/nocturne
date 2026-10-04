<script lang="ts">
  import SubmitButton from "$lib/forms/SubmitButton.svelte";
  import Check from "@lucide/svelte/icons/check";
  import MessageSquareText from "@lucide/svelte/icons/message-square-text";
  import { copyToClipboard } from "$lib/utils";

  interface Props {
    url: string;
    inviterName?: string;
    onCopied: () => void;
    onCopyFailed: () => void;
  }

  let { url, inviterName, onCopied, onCopyFailed }: Props = $props();

  let copied = $state(false);
  let copies = $state(0);

  const message = $derived(
    inviterName
      ? `${inviterName} has invited you to Nocturne, a website for viewing diabetes information, such as glucose readings, that they've chosen to share with you. Open this link to sign in or create an account and join: ${url}`
      : `You've been invited to Nocturne, a website for viewing diabetes information, such as glucose readings, that someone has chosen to share with you. Open this link to sign in or create an account and join: ${url}`
  );

  async function copyMessage() {
    if (!(await copyToClipboard(message))) {
      onCopyFailed();
      return;
    }
    onCopied();
    copies += 1;
    copied = true;
    setTimeout(() => (copied = false), 2000);
  }
</script>

<SubmitButton
  type="button"
  variant="outline"
  saved={copies}
  class="w-full"
  onclick={copyMessage}
>
  {#if copied}
    <Check class="h-4 w-4 text-success" />
    Message copied
  {:else}
    <MessageSquareText class="h-4 w-4" />
    Copy invitation message
  {/if}
</SubmitButton>
