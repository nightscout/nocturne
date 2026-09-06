<script lang="ts">
  import { KeyRound, X } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { listCredentials } from "$lib/api/generated/passkeys.generated.remote";
  import {
    getAll,
    updateStatus,
  } from "$lib/api/generated/coachMarks.generated.remote";

  // Persisted with the coach marks, so the dismissal is stored per subject per
  // tenant rather than per browser.
  const MARK_KEY = "account.backup-sign-in";

  const credentialsQuery = listCredentials();
  const marksQuery = getAll();

  const hasSingleSignInMethod = $derived(
    credentialsQuery.current?.hasSingleSignInMethod === true
  );
  const dismissed = $derived(
    marksQuery.current?.some(
      (mark) => mark.markKey === MARK_KEY && mark.status === "dismissed"
    ) === true
  );

  async function dismiss() {
    try {
      await updateStatus({ key: MARK_KEY, request: { status: "dismissed" } });
    } catch (err) {
      console.error("Failed to dismiss the backup sign-in prompt:", err);
    }
  }
</script>

{#if hasSingleSignInMethod && !dismissed}
  <div
    class="sticky top-0 z-50 flex items-start justify-between gap-4 border-b border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200"
  >
    <div class="flex items-start gap-2">
      <KeyRound class="mt-0.5 h-4 w-4 shrink-0" />
      <div class="space-y-0.5">
        <p class="font-medium">Add a backup way to sign in</p>
        <p>
          There is one way into this account. Add a second passkey, link a
          sign-in method, or save recovery codes &mdash; one-time codes you can
          type in if you lose your device.
        </p>
      </div>
    </div>
    <div class="flex shrink-0 items-center gap-1">
      <Button size="sm" variant="outline" href="/settings/account">
        Account settings
      </Button>
      <Button size="sm" variant="ghost" aria-label="Dismiss" onclick={dismiss}>
        <X class="h-3 w-3" />
      </Button>
    </div>
  </div>
{/if}
