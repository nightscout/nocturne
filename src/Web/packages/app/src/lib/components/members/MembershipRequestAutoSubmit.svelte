<script lang="ts">
  import { browser } from "$app/environment";
  import { onMount } from "svelte";
  import { createRequest } from "$lib/api/generated/membershipRequests.generated.remote";
  import { membershipRequestStorageKey } from "$lib/membership-request-storage";

  interface Props {
    isAuthenticated: boolean;
    isGuestSession: boolean;
  }

  const { isAuthenticated, isGuestSession }: Props = $props();

  onMount(async () => {
    if (!browser || !isAuthenticated || isGuestSession) return;

    const key = membershipRequestStorageKey(window.location.host);
    const message = localStorage.getItem(key);
    if (message === null) return;

    try {
      await createRequest({ message: message || undefined });
    } catch {
      // Silently handle — user may already be a member or have a pending request
    } finally {
      localStorage.removeItem(key);
    }
  });
</script>
