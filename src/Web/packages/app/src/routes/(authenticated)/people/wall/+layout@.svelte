<script lang="ts">
  import type { Snippet } from "svelte";
  import { invalidate } from "$app/navigation";
  import AlertSurfaces from "#lib/components/alerts/AlertSurfaces.svelte";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { SHELL_DATA_KEY, SHELL_REVALIDATE_INTERVAL_MS } from "#lib/constants/shell-data.js";
  import { refreshOnVisible } from "#lib/utils/refresh-on-return.svelte.js";
  import { createShellRevalidator } from "#lib/utils/shell-session.js";
  import type { LayoutData } from "./$types";

  const { data, children }: { data: LayoutData; children: Snippet } = $props();

  refreshOnVisible(
    createShellRevalidator(
      () => data,
      () => void invalidate(SHELL_DATA_KEY)
    ),
    SHELL_REVALIDATE_INTERVAL_MS
  );
</script>

{#if !data.tenantless && satisfiesScope(data.effectivePermissions ?? [], "alerts.read")}
  <div class="fixed inset-x-0 top-0 z-60">
    <AlertSurfaces />
  </div>
{/if}

{@render children()}
