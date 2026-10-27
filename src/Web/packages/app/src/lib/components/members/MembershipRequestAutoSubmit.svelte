<script lang="ts">
  import { browser } from "$app/env";
  import { replaceState } from "$app/navigation";
  import { page } from "$app/state";
  import { onMount } from "svelte";
  import { createRequest } from "#lib/api/generated/membershipRequests.generated.remote.js";
  import { takeMembershipRequest } from "#lib/membership-request-storage.js";

  interface Props {
    isAuthenticated: boolean;
    isGuestSession: boolean;
  }

  const { isAuthenticated, isGuestSession }: Props = $props();

  onMount(async () => {
    if (!browser || !isAuthenticated || isGuestSession) return;

    const { message, rest } = takeMembershipRequest(
      sessionStorage,
      localStorage,
      page.url.host,
      page.url,
      Date.now(),
    );
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL minus one param, already resolved
    if (rest) replaceState(rest, page.state);
    if (message === null) return;

    try {
      await createRequest({ message: message || undefined });
    } catch {
      // Silent: the user may already be a member or have a pending request.
    }
  });
</script>
