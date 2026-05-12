<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { ArrowRight } from "@lucide/svelte";
</script>

<div class="mb-8">
    <ul class="list-disc list-inside space-y-2 text-muted-foreground mb-6">
        <li>Configure your data connectors (Dexcom, LibreLinkUp, Glooko, etc.) via the admin panel</li>
        <li>Set up a reverse proxy (Nginx, Caddy, Traefik) for HTTPS</li>
        <li>Point your CGM app or loop system to your new Nocturne instance</li>
        <li>Watchtower is included and checks for image updates daily — no action required</li>
    </ul>

    <div class="flex gap-4 not-prose">
        <Button href="/docs/configuration" variant="outline" class="gap-2">
            Configuration Guide
            <ArrowRight class="w-4 h-4" />
        </Button>
    </div>
</div>
