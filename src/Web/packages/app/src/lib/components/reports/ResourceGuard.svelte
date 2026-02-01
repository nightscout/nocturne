<script lang="ts">
  import { AlertTriangle, RefreshCw } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import ReportsSkeleton from "./ReportsSkeleton.svelte";
  import type { Snippet } from "svelte";

  interface Props {
    /** Whether the resource is loading */
    loading: boolean;
    /** Error object or message, if any */
    error: Error | string | null | undefined;
    /** Whether there is cached data to show (prevents skeleton flash) */
    hasData?: boolean;
    /** Title for error card */
    errorTitle?: string;
    /** Function to call for retry */
    onRetry?: () => void;
    /** Use compact inline error display instead of full-page card */
    compact?: boolean;
    /** Content to render when loaded successfully */
    children: Snippet;
    /** Optional custom loading snippet */
    loadingSnippet?: Snippet;
    /** Optional custom error snippet - receives errorMessage and onRetry */
    errorSnippet?: Snippet<[{ message: string; retry?: () => void }]>;
  }

  let {
    loading,
    error,
    hasData = false,
    errorTitle = "Error Loading Data",
    onRetry,
    compact = false,
    children,
    loadingSnippet,
    errorSnippet,
  }: Props = $props();

  const errorMessage = $derived(
    error
      ? error instanceof Error
        ? error.message
        : String(error)
      : null
  );

  const showSkeleton = $derived(loading && !hasData);
  const showError = $derived(!showSkeleton && !!errorMessage);
  const showContent = $derived(!showSkeleton && !showError);
</script>

{#if showSkeleton}
  {#if loadingSnippet}
    {@render loadingSnippet()}
  {:else}
    <ReportsSkeleton />
  {/if}
{:else if showError && errorMessage}
  {#if errorSnippet}
    {@render errorSnippet({ message: errorMessage, retry: onRetry })}
  {:else if compact}
    <div class="flex h-full min-h-[200px] items-center justify-center p-6 text-center">
      <div>
        <AlertTriangle class="mx-auto h-10 w-10 text-destructive opacity-50" />
        <p class="mt-2 font-medium text-destructive">{errorMessage}</p>
        {#if onRetry}
          <Button
            variant="outline"
            size="sm"
            class="mt-3"
            onclick={onRetry}
          >
            <RefreshCw class="mr-2 h-4 w-4" />
            Try again
          </Button>
        {/if}
      </div>
    </div>
  {:else}
    <div class="container mx-auto max-w-7xl px-4 py-6">
      <Card class="border-2 border-destructive">
        <CardHeader>
          <CardTitle class="flex items-center gap-2 text-destructive">
            <AlertTriangle class="h-5 w-5" />
            {errorTitle}
          </CardTitle>
        </CardHeader>
        <CardContent>
          <p class="text-destructive-foreground">{errorMessage}</p>
          {#if onRetry}
            <Button
              variant="outline"
              class="mt-4"
              onclick={onRetry}
            >
              Try again
            </Button>
          {/if}
        </CardContent>
      </Card>
    </div>
  {/if}
{:else if showContent}
  {@render children()}
{/if}
