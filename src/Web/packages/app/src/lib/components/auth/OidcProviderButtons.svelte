<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { ExternalLink, Loader2 } from "lucide-svelte";
  import type { OidcProviderInfo } from "$lib/api/generated/nocturne-api-client";

  interface Props {
    providers: OidcProviderInfo[];
    disabled?: boolean;
    onLogin: (providerId: string) => void;
    isRedirecting?: boolean;
    selectedProvider?: string | null;
    dividerText?: string;
    showDivider?: boolean;
  }

  let {
    providers,
    disabled = false,
    onLogin,
    isRedirecting = false,
    selectedProvider = null,
    dividerText = "Or create an account with a passkey",
    showDivider = true,
  }: Props = $props();

  function getButtonStyle(buttonColor?: string): string {
    if (!buttonColor) return "";
    return `background-color: ${buttonColor}; border-color: ${buttonColor};`;
  }
</script>

<div class="space-y-3">
  {#each providers as provider}
    <Button
      variant="outline"
      size="lg"
      class="w-full relative"
      style={getButtonStyle(provider.buttonColor)}
      {disabled}
      onclick={() => provider.id && onLogin(provider.id)}
    >
      {#if isRedirecting && selectedProvider === provider.id}
        <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        Redirecting...
      {:else}
        <ExternalLink class="mr-2 h-4 w-4" />
        Continue with {provider.name}
      {/if}
    </Button>
  {/each}
</div>

{#if showDivider}
  <div class="relative">
    <div class="absolute inset-0 flex items-center">
      <span class="w-full border-t"></span>
    </div>
    <div class="relative flex justify-center text-xs uppercase">
      <span class="bg-background px-2 text-muted-foreground">
        {dividerText}
      </span>
    </div>
  </div>
{/if}
