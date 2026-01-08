<script lang="ts">
    import { AlertCircle } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <h1 class="text-4xl font-bold tracking-tight mb-4">Configuration Guide</h1>
    <p class="text-lg text-muted-foreground mb-8">
        Learn how to configure connectors, settings, and customize your Nocturne instance.
    </p>

    <div
        class="p-4 rounded-lg border border-amber-500/30 bg-amber-500/5 mb-8 not-prose"
    >
        <div class="flex items-start gap-3">
            <AlertCircle class="w-5 h-5 text-amber-500 mt-0.5 shrink-0" />
            <p class="text-sm text-muted-foreground">
                <strong class="text-amber-700 dark:text-amber-400">Placeholder content.</strong>
                This guide will be expanded with detailed instructions.
            </p>
        </div>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">Environment Variables</h2>
    <p class="text-muted-foreground mb-4">
        Nocturne is configured primarily through environment variables. Key settings include:
    </p>

    <div class="overflow-x-auto">
        <table class="w-full text-sm">
            <thead>
                <tr class="border-b border-border">
                    <th class="text-left py-2 pr-4 font-semibold">Variable</th>
                    <th class="text-left py-2 font-semibold">Description</th>
                </tr>
            </thead>
            <tbody class="text-muted-foreground">
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4 font-mono text-xs">API_SECRET</td>
                    <td class="py-2">Your API secret for authentication</td>
                </tr>
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4 font-mono text-xs">DATABASE_URL</td>
                    <td class="py-2">PostgreSQL connection string</td>
                </tr>
                <tr class="border-b border-border/50">
                    <td class="py-2 pr-4 font-mono text-xs">DISPLAY_UNITS</td>
                    <td class="py-2">mg/dL or mmol/L</td>
                </tr>
                <tr>
                    <td class="py-2 pr-4 font-mono text-xs">TIME_FORMAT</td>
                    <td class="py-2">12 or 24 hour format</td>
                </tr>
            </tbody>
        </table>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">Data Connectors</h2>
    <p class="text-muted-foreground mb-4">
        Connectors pull data from external sources. Each connector has its own configuration:
    </p>

    <h3 class="text-xl font-semibold mt-6 mb-3">Dexcom Share</h3>
    <p class="text-muted-foreground mb-2">
        Connect to Dexcom Share to automatically sync your G6, G7, or ONE data.
    </p>
    <ul class="list-disc list-inside text-muted-foreground space-y-1 text-sm">
        <li>Requires Dexcom account username and password</li>
        <li>Select US or International server based on your account region</li>
        <li>Data syncs every 5 minutes</li>
    </ul>

    <h3 class="text-xl font-semibold mt-6 mb-3">LibreView / LibreLinkUp</h3>
    <p class="text-muted-foreground mb-2">
        Connect to Abbott's LibreView or LibreLinkUp for Freestyle Libre data.
    </p>
    <ul class="list-disc list-inside text-muted-foreground space-y-1 text-sm">
        <li>Requires LibreView/LibreLinkUp credentials</li>
        <li>Supports Libre 2 and Libre 3 sensors</li>
    </ul>

    <h3 class="text-xl font-semibold mt-6 mb-3">Nightscout Proxy</h3>
    <p class="text-muted-foreground mb-2">
        Pull data from an existing Nightscout instance.
    </p>
    <ul class="list-disc list-inside text-muted-foreground space-y-1 text-sm">
        <li>Requires Nightscout URL and API secret</li>
        <li>Great for running Nocturne alongside existing Nightscout</li>
    </ul>

    <h2 class="text-2xl font-bold mt-8 mb-4">Nightscout Compatibility</h2>
    <p class="text-muted-foreground mb-4">
        Nocturne aims for full compatibility with Nightscout clients. To configure
        your existing apps:
    </p>
    <ol class="list-decimal list-inside space-y-2 text-muted-foreground">
        <li>Change the Nightscout URL to your Nocturne instance</li>
        <li>Use the same API secret you configured for Nocturne</li>
        <li>Enable any required plugins in the Nocturne settings</li>
    </ol>

    <h2 class="text-2xl font-bold mt-8 mb-4">Advanced Configuration</h2>
    <p class="text-muted-foreground">
        For advanced configuration options including HTTPS setup, reverse proxy
        configuration, and multi-user setups, see the detailed guides in the
        documentation.
    </p>
</div>
