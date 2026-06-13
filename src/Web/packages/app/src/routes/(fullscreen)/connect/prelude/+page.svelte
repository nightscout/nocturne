<script lang="ts">
  import { onMount } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { ExternalLink, Smartphone, Copy, Check } from "lucide-svelte";
  import { buildPreludeDeepLink } from "$lib/utils/prelude-links";

  let viewState: "redirecting" | "fallback" = $state("redirecting");
  let copied = $state(false);
  let copyTimeout: ReturnType<typeof setTimeout> | null = null;

  const instanceUrl = typeof window !== "undefined" ? window.location.origin : "";
  const deepLink = buildPreludeDeepLink(instanceUrl);

  onMount(() => {
    window.location.href = deepLink;
    const timeout = setTimeout(() => {
      viewState = "fallback";
    }, 2000);
    return () => {
      clearTimeout(timeout);
      if (copyTimeout) clearTimeout(copyTimeout);
    };
  });

  async function copyUrl() {
    try {
      await navigator.clipboard.writeText(instanceUrl);
      copied = true;
      if (copyTimeout) clearTimeout(copyTimeout);
      copyTimeout = setTimeout(() => (copied = false), 2000);
    } catch {
      // Clipboard API unavailable; user can still long-press the code block.
    }
  }
</script>

<div class="flex min-h-screen items-center justify-center p-4">
  {#if viewState === "redirecting"}
    <Card class="w-full max-w-md text-center">
      <CardHeader>
        <CardTitle>Opening Prelude</CardTitle>
        <CardDescription>Redirecting to Prelude to start the connection...</CardDescription>
      </CardHeader>
    </Card>
  {:else}
    <Card class="w-full max-w-md">
      <CardHeader>
        <CardTitle>Connect Prelude</CardTitle>
        <CardDescription>
          Prelude didn't open automatically. You can try these options:
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <Button href={deepLink} class="w-full">
          <Smartphone class="mr-2 h-4 w-4" />
          Open in Prelude
        </Button>

        <div class="space-y-2">
          <p class="text-sm font-medium">Manual setup</p>
          <p class="text-muted-foreground text-sm">
            In Prelude, open Settings &gt; Account &gt; Connect with QR (or use Manual setup),
            and enter this URL:
          </p>
          <div class="flex items-center gap-2">
            <code class="bg-muted flex-1 rounded px-3 py-2 text-sm break-all">
              {instanceUrl}
            </code>
            <Button variant="ghost" size="icon" onclick={copyUrl}>
              {#if copied}
                <Check class="h-4 w-4" />
              {:else}
                <Copy class="h-4 w-4" />
              {/if}
            </Button>
          </div>
          <p class="text-muted-foreground text-sm">
            Then approve the code Prelude shows you back on this site.
          </p>
        </div>

        <div class="pt-2">
          <p class="text-muted-foreground text-sm">Don't have Prelude installed?</p>
          <Button
            href="https://github.com/nightscout/prelude/releases"
            target="_blank"
            variant="link"
            class="px-0"
          >
            <ExternalLink class="mr-1 h-3 w-3" />
            Download Prelude
          </Button>
        </div>
      </CardContent>
    </Card>
  {/if}
</div>
