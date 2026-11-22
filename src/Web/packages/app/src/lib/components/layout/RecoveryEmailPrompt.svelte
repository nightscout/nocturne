<script lang="ts">
  import Mail from "@lucide/svelte/icons/mail";
  import X from "@lucide/svelte/icons/x";
  import { Banner } from "#lib/components/ui/banner/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { updateStatus } from "#lib/api/generated/coachMarks.generated.remote.js";

  // Persisted with the coach marks. The API hides the reminder for 30 days after a dismissal.
  const MARK_KEY = "account.recovery-email";

  interface Props {
    show: boolean;
    /** The account holds an address that is not confirmed yet. */
    hasAddress?: boolean;
  }

  let { show, hasAddress = false }: Props = $props();

  let dismissedHere = $state(false);

  async function dismiss() {
    dismissedHere = true;
    try {
      await updateStatus({ key: MARK_KEY, request: { status: "dismissed" } });
    } catch (err) {
      dismissedHere = false;
      console.error("Failed to dismiss the recovery email reminder:", err);
    }
  }
</script>

{#if show && !dismissedHere}
  <Banner variant="warning" class="flex-col items-start @xl:flex-row">
    <div class="flex items-start gap-2">
      <Mail class="mt-0.5 h-4 w-4 shrink-0" />
      <div class="space-y-0.5">
        {#if hasAddress}
          <p class="font-medium">Confirm your recovery email</p>
          <p>
            Your recovery email is not confirmed. Open recovery email settings to send a new link.
          </p>
        {:else}
          <p class="font-medium">Add a recovery email</p>
          <p>
            If you lose your sign-in, we send a link to your recovery email so you can get back in. Add an address that only you can read.
          </p>
        {/if}
      </div>
    </div>
    <div class="ml-6 flex shrink-0 items-center gap-1 @xl:ml-0">
      <Button size="sm" variant="outline" href="/settings/account#recovery-email">
        Recovery email settings
      </Button>
      <Button size="sm" variant="ghost" aria-label="Dismiss" onclick={dismiss}>
        <X class="h-3 w-3" />
      </Button>
    </div>
  </Banner>
{/if}
