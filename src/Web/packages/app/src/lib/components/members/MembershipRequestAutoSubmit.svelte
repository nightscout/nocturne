<script lang="ts">
  import { browser } from "$app/environment";
  import { onMount } from "svelte";
  import { createRequest } from "$lib/api/generated/membershipRequests.generated.remote";

  interface Props {
    isAuthenticated: boolean;
    isGuestSession: boolean;
    tenantSlug: string | undefined;
  }

  const { isAuthenticated, isGuestSession, tenantSlug }: Props = $props();

  const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

  onMount(async () => {
    if (!browser || !tenantSlug || !isAuthenticated || isGuestSession) return;

    const key = `${STORAGE_KEY_PREFIX}${tenantSlug}`;
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
