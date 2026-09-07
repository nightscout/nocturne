<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight } from "@lucide/svelte";
</script>

<div class="mb-8">
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-6">
        <li>
            Open <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">https://your-domain</code>
            and complete first-run setup, which creates the owner account with a
            <a href="/docs/authentication/passkeys" class="text-primary hover:underline">passkey</a>
            or a sign-in provider.
        </li>
        <li>
            Connect a data source (Dexcom, LibreLinkUp, CareLink, Glooko, or your existing Nightscout)
            under <strong>Settings &rsaquo; Connectors &amp; Apps</strong>.
        </li>
        <li>
            Point your CGM app or loop system at your new Nocturne URL. See
            <a href="/docs/connecting-apps" class="text-primary hover:underline">Connecting apps</a>.
        </li>
        <li>
            Invite the people who follow you, or create a
            <a href="/docs/sharing" class="text-primary hover:underline">share link</a>.
        </li>
        <li>Watchtower is included and checks for image updates daily; no action required.</li>
    </ul>

    <div class="flex gap-4 not-prose">
        <Button href="/docs/configuration" variant="outline" class="gap-2">
            Configuration Guide
            <ArrowRight class="w-4 h-4" />
        </Button>
    </div>
</div>
