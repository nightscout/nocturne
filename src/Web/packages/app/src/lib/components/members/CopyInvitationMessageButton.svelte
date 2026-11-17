<script lang="ts">
  import SubmitButton from "#lib/forms/SubmitButton.svelte";
  import Check from "@lucide/svelte/icons/check";
  import MessageSquareText from "@lucide/svelte/icons/message-square-text";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";

  interface Props {
    url: string;
    inviterName?: string;
  }

  let { url, inviterName }: Props = $props();

  const copy = createCopyFeedback();
  let copies = $state(0);

  const message = $derived(
    inviterName
      ? `${inviterName} has invited you to Nocturne, a website for viewing diabetes information, such as glucose readings, that they chose to share with you. Open this link to sign in or create an account and join: ${url}`
      : `You are invited to Nocturne, a website for viewing diabetes information, such as glucose readings, that someone chose to share with you. Open this link to sign in or create an account and join: ${url}`
  );

  async function copyMessage() {
    if (!(await copy.copy(message))) return;
    copies += 1;
  }
</script>

<SubmitButton
  type="button"
  variant="outline"
  saved={copies}
  class="w-full"
  onclick={copyMessage}
>
  {#if copy.isCopied()}
    <Check class="h-4 w-4 text-success" />
    Message copied
  {:else}
    <MessageSquareText class="h-4 w-4" />
    Copy invitation message
  {/if}
</SubmitButton>
