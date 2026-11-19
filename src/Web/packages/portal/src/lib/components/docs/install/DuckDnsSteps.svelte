<p class="text-muted-foreground mb-4">
    <a href="https://www.duckdns.org" class="text-primary hover:underline">DuckDNS</a> is a
    free dynamic DNS service that gives out names under <strong>duckdns.org</strong>, each
    with a wildcard for the subdomains below it. The installer uses your DuckDNS token to
    point your name at your server, so there is nothing to configure by hand.
</p>
<ol class="list-decimal list-inside space-y-2 text-muted-foreground mb-8">
    <li>Sign in at duckdns.org with one of the accounts it offers, such as GitHub or Google.</li>
    <li>
        Under <strong>domains</strong>, type a name and select <strong>add domain</strong>.
        Your domain is then
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">yourname.duckdns.org</code>.
    </li>
    <li>
        Copy the <strong>token</strong> shown at the top of the page. It looks like
        <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded">01234567-89ab-cdef-0123-456789abcdef</code>.
        It controls every name on your account, so keep it to yourself.
    </li>
</ol>
