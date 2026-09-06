<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { ExternalLink } from "@lucide/svelte";
    import { DEMO_ENABLED, DEMO_WEB_URL } from "$lib/config";
</script>

<svelte:head>
    <title>Demo - Nocturne</title>
</svelte:head>

<div class="container mx-auto px-4 py-20">
    {#if DEMO_ENABLED && DEMO_WEB_URL}
        <div class="max-w-xl mx-auto text-center">
            <h1 class="text-3xl font-bold mb-4">Live demo</h1>
            <p class="text-muted-foreground mb-6">
                A full Nocturne instance you can explore and change — add treatments, edit
                settings, browse reports. It runs on made-up data, not anyone's real
                readings, and it resets on a schedule, so you can click anything.
            </p>

            <!--
                Opens in a new tab rather than an embedded frame. The demo signs visitors in
                with a session cookie and runs on a different domain to this site, so a
                browser will not send that cookie to a frame embedded here.
            -->
            <Button
                href={DEMO_WEB_URL}
                target="_blank"
                rel="noopener noreferrer"
                size="lg"
                class="gap-2 text-base"
            >
                Open the demo
                <ExternalLink class="size-4" />
            </Button>

            <p class="text-sm text-muted-foreground mt-6">
                Everyone shares the same demo instance, so you may see changes other
                visitors have made.
            </p>
        </div>
    {:else}
        <div class="max-w-md mx-auto text-center">
            <h1 class="text-2xl font-bold mb-4">Demo not available</h1>
            <p class="text-muted-foreground mb-6">
                The demo instance is not currently enabled.
            </p>
            <Button href="/" variant="outline">Back to home</Button>
        </div>
    {/if}
</div>
