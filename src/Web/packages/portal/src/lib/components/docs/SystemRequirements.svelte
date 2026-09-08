<div class="overflow-x-auto mb-4">
    <table class="w-full text-sm">
        <thead>
            <tr class="border-b border-border">
                <th class="text-left py-2 pr-4 font-semibold">Component</th>
                <th class="text-left py-2 pr-4 font-semibold">Minimum</th>
                <th class="text-left py-2 font-semibold">Recommended</th>
            </tr>
        </thead>
        <tbody class="text-muted-foreground">
            <tr class="border-b border-border/50">
                <td class="py-2 pr-4">RAM</td>
                <td class="py-2 pr-4">2 GB</td>
                <td class="py-2">4 GB+</td>
            </tr>
            <tr class="border-b border-border/50">
                <td class="py-2 pr-4">CPU</td>
                <td class="py-2 pr-4">1 core</td>
                <td class="py-2">2 cores+</td>
            </tr>
            <tr class="border-b border-border/50">
                <td class="py-2 pr-4">Storage</td>
                <td class="py-2 pr-4">10 GB</td>
                <td class="py-2">20 GB+</td>
            </tr>
            <tr class="border-b border-border/50">
                <td class="py-2 pr-4">Docker</td>
                <td class="py-2 pr-4">20.10+</td>
                <td class="py-2">Latest</td>
            </tr>
            <tr>
                <td class="py-2 pr-4">Docker Compose</td>
                <td class="py-2 pr-4">2.23.1+</td>
                <td class="py-2">Latest</td>
            </tr>
        </tbody>
    </table>
</div>
<details class="mb-8">
    <summary class="text-sm font-medium text-muted-foreground cursor-pointer hover:text-foreground">Where these numbers come from</summary>
    <div class="text-sm text-muted-foreground mt-2 space-y-2">
        <p>
            Measured on a production deployment serving 76 sites, then scaled down to a
            single-site install. The stack is six containers: PostgreSQL, the API, the web
            frontend, the gateway, the TLS proxy and the updater.
        </p>
        <p>
            <strong>Memory.</strong> Those six use roughly 1 to 1.5 GB between them at rest,
            most of it PostgreSQL and the API. 2 GB leaves little spare once the operating
            system is accounted for, which is why it is the floor rather than a comfortable
            target; the extra memory at 4 GB is used by PostgreSQL as disk cache and is the
            single cheapest thing you can give it.
        </p>
        <p>
            <strong>CPU.</strong> The API is the only component that does sustained work. One
            core is enough for a single site, and the whole 76-site deployment above draws
            about 1.3 cores in total, so headroom here matters less than it looks.
        </p>
        <p>
            <strong>Storage.</strong> The container images account for about 2.5 GB before any
            data. A single CGM writes roughly 105,000 glucose readings a year, which with
            indexes and write-ahead logs comes to well under 1 GB per year. 10 GB fits the
            images and several years of one person's data; 20 GB is for households, longer
            history, or an imported Nightscout database.
        </p>
    </div>
</details>
