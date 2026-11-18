<script lang="ts">
    import Download from "@lucide/svelte/icons/download";
    import { Button } from "@nocturne/ui/ui/button";
    import { Checkbox } from "@nocturne/ui/ui/checkbox";
    import { Label } from "@nocturne/ui/ui/label";
    import Callout from "@nocturne/cms/components/Callout.svelte";
    import { getInstallGuide } from "./install/guide.svelte.js";

    // The file comes from the page's install guide, which was given the release's .env.example.
    const guide = getInstallGuide();
</script>

{#if guide}
    <div class="not-prose mb-8 space-y-4">
        {#if !guide.domainValid}
            <div class="flex flex-wrap items-center gap-3 rounded-lg border border-border/60 bg-muted/50 p-3">
                <p class="m-0 flex-1 basis-64 text-sm text-muted-foreground">
                    The file contains your domain. Enter your domain in the bar at the top of the
                    page first.
                </p>
                <Button variant="outline" size="sm" onclick={() => guide.focusDomain()}>
                    Enter your domain
                </Button>
            </div>
        {/if}

        <Button variant="outline" onclick={() => guide.download()} disabled={!guide.envFile || !guide.domainValid}>
            <Download class="size-4" />
            Download {guide.fileName}
        </Button>

        <details>
            <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">
                Show the file
            </summary>
            <div class="mt-2 rounded-lg border border-border/60 bg-muted/50">
                <pre class="max-h-96 overflow-auto p-3 text-sm"><code
                        >{guide.envFile ?? "Your browser makes the values when the page loads."}</code
                    ></pre>
            </div>
        </details>

        <Callout type="warning" title="Keep a copy of this file">
            <p>
                WARNING: Keep <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">INSTANCE_KEY</code>.
                Nocturne uses it to encrypt the passwords and secrets for your alert channels,
                webhooks and connectors. If you lose it, alerts stop at those channels and connectors
                stop until you enter each secret again.
            </p>
            <p>
                This page makes new values each time it loads. Do not make a new file for an
                installation that already runs. The database keeps the passwords from its first start.
            </p>
        </Callout>

        <div class="flex items-start gap-2">
            <Checkbox id="env-saved" bind:checked={guide.saved} disabled={!guide.downloadCurrent} class="mt-0.5" />
            <Label for="env-saved" variant="option">
                <span class="leading-snug">
                    I saved a copy of this file in a location that is not this server, for example
                    a password manager.
                </span>
            </Label>
        </div>
    </div>
{/if}
