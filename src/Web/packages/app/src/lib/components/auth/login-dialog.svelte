<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { invalidateAll } from "$app/navigation";
  import LoginForm from "./LoginForm.svelte";

  let {
    open = $bindable(false),
    onSuccess,
    onCancel,
  }: {
    open?: boolean;
    onSuccess?: () => void;
    onCancel?: () => void;
  } = $props();

  /** Handle successful login */
  async function handleLoginSuccess() {
    // Invalidate all data to reload with new auth state
    await invalidateAll();

    // Close the dialog
    open = false;

    // Call the success callback
    onSuccess?.();
  }

  /** Handle dialog open/close state changes */
  function handleOpenChange(newOpen: boolean) {
    open = newOpen;

    // If closing without success, call onCancel
    if (!newOpen) {
      setTimeout(() => {
        if (!open) {
          onCancel?.();
        }
      }, 0);
    }
  }
</script>

<Dialog.Root {open} onOpenChange={handleOpenChange}>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title>Sign in required</Dialog.Title>
      <Dialog.Description>Sign in to continue</Dialog.Description>
    </Dialog.Header>

    <LoginForm
      returnUrl={typeof window !== 'undefined'
        ? window.location.pathname + window.location.search
        : '/'}
      onSuccess={handleLoginSuccess}
    />
  </Dialog.Content>
</Dialog.Root>
