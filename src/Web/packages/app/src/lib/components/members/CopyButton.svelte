<script lang="ts">
  import Check from "@lucide/svelte/icons/check";
  import Copy from "@lucide/svelte/icons/copy";
  import SubmitButton from "$lib/forms/SubmitButton.svelte";
  import { createCopyFeedback } from "$lib/hooks/copy-feedback.svelte";

  interface Props {
    text: string;
    /** The accessible name; the button shows only an icon. */
    label: string;
    class?: string;
    oncopied?: () => void;
  }

  let { text, label, class: className, oncopied }: Props = $props();

  const copy = createCopyFeedback();
  let copies = $state(0);

  async function handleCopy() {
    if (!(await copy.copy(text))) return;
    copies += 1;
    oncopied?.();
  }
</script>

<SubmitButton
  type="button"
  variant="outline"
  size="icon"
  saved={copies}
  aria-label={label}
  class={className}
  onclick={handleCopy}
>
  {#if copy.isCopied()}
    <Check class="h-4 w-4 text-success" />
  {:else}
    <Copy class="h-4 w-4" />
  {/if}
</SubmitButton>
