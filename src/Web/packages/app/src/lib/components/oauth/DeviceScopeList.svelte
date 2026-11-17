<script lang="ts">
  import Check from "@lucide/svelte/icons/check";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import {
    getOAuthScopeDescription,
    isSensitiveDeviceScope,
  } from "#lib/constants/oauth-scopes.js";

  let { scopes }: { scopes: string[] } = $props();
</script>

<ul class="space-y-2">
  {#each scopes as scope, i (i)}
    {#if isSensitiveDeviceScope(scope)}
      <li class="flex items-start gap-3 text-sm">
        <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
        <span class="font-medium text-destructive">
          {getOAuthScopeDescription(scope)}
        </span>
      </li>
    {:else}
      <li class="flex items-start gap-3 text-sm">
        <Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
        <span class="text-muted-foreground">
          {getOAuthScopeDescription(scope)}
        </span>
      </li>
    {/if}
  {/each}
</ul>
