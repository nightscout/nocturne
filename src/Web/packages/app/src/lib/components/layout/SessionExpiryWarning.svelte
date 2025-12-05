<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Alert from "$lib/components/ui/alert";
  import { Clock, RefreshCw, X } from "lucide-svelte";
  import { formatSessionExpiry } from "$lib/stores/auth.svelte";
  import { refreshSession } from "../../../routes/auth/auth.remote";

  interface Props {
    /**
     * Time until session expires in seconds
     */
    timeUntilExpiry: number;
    /**
     * Callback when user clicks refresh
     */
    onRefresh?: () => void;
    /**
     * Callback when user dismisses the warning
     */
    onDismiss?: () => void;
  }

  const { timeUntilExpiry, onRefresh, onDismiss }: Props = $props();

  let isRefreshing = $state(false);

  async function handleRefresh() {
    if (isRefreshing) return;

    isRefreshing = true;

    try {
      const result = await refreshSession();

      if (result.success) {
        onRefresh?.();
      } else {
        // Refresh failed, redirect to login
        window.location.href = "/auth/login";
      }
    } catch {
      window.location.href = "/auth/login";
    } finally {
      isRefreshing = false;
    }
  }
</script>

{#if timeUntilExpiry > 0 && timeUntilExpiry < 300}
  <div class="fixed bottom-4 right-4 z-50 max-w-sm animate-in slide-in-from-bottom-4">
    <Alert.Root variant="default" class="border-yellow-500 bg-yellow-50 dark:bg-yellow-950/50">
      <Clock class="h-4 w-4 text-yellow-600 dark:text-yellow-500" />
      <Alert.Title class="text-yellow-800 dark:text-yellow-200">Session Expiring</Alert.Title>
      <Alert.Description class="text-yellow-700 dark:text-yellow-300">
        Your session will expire in {formatSessionExpiry(timeUntilExpiry)}.
        Click refresh to extend your session.
      </Alert.Description>
      <div class="mt-3 flex gap-2">
        <Button size="sm" onclick={handleRefresh} disabled={isRefreshing}>
          <RefreshCw class="mr-1 h-3 w-3 {isRefreshing ? 'animate-spin' : ''}" />
          {isRefreshing ? "Refreshing..." : "Refresh Session"}
        </Button>
        {#if onDismiss}
          <Button size="sm" variant="ghost" onclick={onDismiss}>
            <X class="h-3 w-3" />
          </Button>
        {/if}
      </div>
    </Alert.Root>
  </div>
{/if}
