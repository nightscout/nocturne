<script lang="ts">
  import KeyRound from "@lucide/svelte/icons/key-round";
  import X from "@lucide/svelte/icons/x";
  import { Banner } from "#lib/components/ui/banner/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { updateStatus } from "#lib/api/generated/coachMarks.generated.remote.js";

  // Persisted with the coach marks, so the dismissal is stored per subject per
  // tenant rather than per browser.
  const MARK_KEY = "account.backup-sign-in";

  interface Props {
    show: boolean;
  }

  let { show }: Props = $props();

  let dismissedHere = $state(false);

  async function dismiss() {
    dismissedHere = true;
    try {
      await updateStatus({ key: MARK_KEY, request: { status: "dismissed" } });
    } catch (err) {
      dismissedHere = false;
      console.error("Failed to dismiss the backup sign-in prompt:", err);
    }
  }
</script>

{#if show && !dismissedHere}
  <Banner variant="warning" class="flex-col items-start @xl:flex-row">
    <div class="flex items-start gap-2">
      <KeyRound class="mt-0.5 h-4 w-4 shrink-0" />
      <div class="space-y-0.5">
        <p class="font-medium">Add a backup way to sign in</p>
        <p>
          There is one way into this account. Add a second passkey, link a sign-in method, or save recovery codes. Recovery codes are one-time codes you can type in if you lose your device.
        </p>
      </div>
    </div>
    <div class="ml-6 flex shrink-0 items-center gap-1 @xl:ml-0">
      <Button size="sm" variant="outline" href="/settings/account">
        Account settings
      </Button>
      <Button size="sm" variant="ghost" aria-label="Dismiss" onclick={dismiss}>
        <X class="h-3 w-3" />
      </Button>
    </div>
  </Banner>
{/if}
