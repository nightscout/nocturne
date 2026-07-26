<script lang="ts">
  // CareLink browser-based sign-in (manual-paste OAuth).
  // Medtronic's Auth0 only allows the com.medtronic.carepartner:/sso custom-scheme redirect and
  // headless login is CAPTCHA-blocked, so the user authenticates in their own browser and pastes the
  // resulting code back. The server exchanges it (PKCE) for a refresh token stored as the connector secret.
  //
  import {
    start as startCareLinkConnect,
    complete as completeCareLinkConnect,
    desktopToken as mintDesktopLinkCode,
  } from "$lib/api/generated/careLinkConnects.generated.remote";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Label } from "$lib/components/ui/label";
  import { CheckCircle2, Copy, Download, ExternalLink, KeyRound, Monitor } from "lucide-svelte";
  import { copyToClipboard } from "$lib/utils";

  // Stable rolling release that always holds the current installers (see desktop-release.yml).
  const DESKTOP_DOWNLOAD_URL =
    "https://github.com/nightscout/nocturne/releases/tag/companion-latest";

  // The custom-scheme URL the user must copy from the redirect (matches the server-side parser).
  const REDIRECT_PREFIX = "com.medtronic.carepartner:/sso";

  /** Pull the authorization code out of whatever the user pastes — a bare code, the full
   *  redirect URL, or a URL with extra query params/whitespace. Mirrors the server's extraction. */
  function extractCode(input: string): string | null {
    const trimmed = input.trim();
    if (!trimmed) return null;
    const m = trimmed.match(/[?&]code=([^&\s]+)/);
    if (m) return decodeURIComponent(m[1]);
    // Allow a bare code paste (no URL), but reject anything that still looks like a URL/garbage.
    if (/^[A-Za-z0-9._~-]+$/.test(trimmed)) return trimmed;
    return null;
  }

  let {
    onConnected,
  }: {
    // Called after a refresh token is stored, with the auto-detected profile (if any).
    onConnected?: (info: { username?: string | null; country?: string | null }) => void;
  } = $props();

  type Phase = "idle" | "awaiting-code" | "done";

  let region = $state<"EU" | "US">("EU");
  let phase = $state<Phase>("idle");
  let flowState = $state<string | null>(null);
  let codeInput = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let connectedUsername = $state<string | null>(null);

  // Resilient to the user pasting the whole redirect URL, a bare code, or extra junk.
  const detectedCode = $derived(extractCode(codeInput));

  async function beginConnect() {
    busy = true;
    error = null;
    try {
      const res = await startCareLinkConnect({ server: region });
      if (!res.authorizeUrl || !res.state) {
        error = "Could not start CareLink sign-in. Please try again.";
        return;
      }
      flowState = res.state;
      phase = "awaiting-code";
      // Open Medtronic's login in a new tab; the user signs in + solves the captcha there.
      window.open(res.authorizeUrl, "_blank", "noopener");
    } catch (e) {
      error = e instanceof Error ? e.message : "Could not start CareLink sign-in.";
    } finally {
      busy = false;
    }
  }

  async function finishConnect() {
    if (!flowState || !detectedCode) return;
    busy = true;
    error = null;
    try {
      const res = await completeCareLinkConnect({
        code: detectedCode,
        state: flowState,
      });
      if (!res.success) {
        error = "Sign-in could not be completed. Please try again.";
        return;
      }
      connectedUsername = res.username ?? null;
      phase = "done";
      onConnected?.({ username: res.username, country: res.country });
    } catch (e) {
      error = e instanceof Error ? e.message : "Could not complete CareLink sign-in.";
    } finally {
      busy = false;
    }
  }

  function reset() {
    phase = "idle";
    flowState = null;
    codeInput = "";
    error = null;
  }

  // Desktop-app handoff: a short-lived link code the user pastes into the companion app,
  // which then runs the CareLink sign-in in its own window and captures the code itself.
  let desktopLinkCode = $state<string | null>(null);
  let desktopExpiresMinutes = $state(10);
  let desktopCopied = $state(false);

  async function generateDesktopLinkCode() {
    busy = true;
    error = null;
    desktopCopied = false;
    try {
      const res = await mintDesktopLinkCode();
      desktopLinkCode = res.linkCode ?? null;
      desktopExpiresMinutes = Math.max(1, Math.round((res.expiresInSeconds ?? 600) / 60));
    } catch (e) {
      error = e instanceof Error ? e.message : "Could not create a link code.";
    } finally {
      busy = false;
    }
  }

  async function copyDesktopLinkCode() {
    if (!desktopLinkCode) return;
    if (!(await copyToClipboard(desktopLinkCode))) {
      error = "Couldn't copy the code to the clipboard. Copy it manually instead.";
      return;
    }
    desktopCopied = true;
    setTimeout(() => (desktopCopied = false), 2000);
  }
</script>

<Card>
  <CardHeader>
    <CardTitle class="flex items-center gap-2">
      <KeyRound class="h-4 w-4" />
      Connect your CareLink account
    </CardTitle>
    <CardDescription>
      Sign in to Medtronic CareLink in your browser. Your password is never sent to Nocturne — only a
      revocable access token is stored.
    </CardDescription>
  </CardHeader>
  <CardContent class="space-y-4">
    {#if error}
      <p class="text-sm text-destructive">{error}</p>
    {/if}

    {#if phase === "idle"}
      <div class="space-y-2">
        <Label>Region</Label>
        <div class="flex gap-2">
          <Button
            variant={region === "EU" ? "default" : "outline"}
            size="sm"
            onclick={() => (region = "EU")}
            disabled={busy}>EU / Outside-US</Button
          >
          <Button
            variant={region === "US" ? "default" : "outline"}
            size="sm"
            onclick={() => (region = "US")}
            disabled={busy}>US</Button
          >
        </div>
        <p class="text-xs text-muted-foreground">
          Australia, NZ, Europe and most of the world use EU. Choose US only for a US CareLink account.
        </p>
      </div>
      <Button onclick={beginConnect} disabled={busy}>
        <ExternalLink class="h-4 w-4 mr-2" />
        {busy ? "Opening…" : "Connect CareLink"}
      </Button>

      <div class="rounded-md border p-3 space-y-2">
        <p class="flex items-center gap-2 text-sm font-medium">
          <Monitor class="h-4 w-4" />
          Using the Nocturne desktop app?
        </p>
        <p class="text-xs text-muted-foreground">
          The desktop app opens the CareLink sign-in in its own window and captures the code
          automatically — no developer tools needed. Generate a link code and paste it into the app.
        </p>
        <a
          href={DESKTOP_DOWNLOAD_URL}
          target="_blank"
          rel="noopener noreferrer"
          class="inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline"
        >
          <Download class="h-3.5 w-3.5" />
          Download the desktop app
        </a>
        {#if !desktopLinkCode}
          <Button variant="outline" size="sm" onclick={generateDesktopLinkCode} disabled={busy}>
            {busy ? "Generating…" : "Generate link code"}
          </Button>
        {:else}
          <div class="rounded bg-muted px-2 py-1 font-mono text-xs break-all">{desktopLinkCode}</div>
          <div class="flex items-center gap-2">
            <Button variant="outline" size="sm" onclick={copyDesktopLinkCode}>
              <Copy class="h-3.5 w-3.5 mr-1" />
              {desktopCopied ? "Copied" : "Copy"}
            </Button>
            <span class="text-xs text-muted-foreground">
              Expires in {desktopExpiresMinutes} minutes.
            </span>
          </div>
        {/if}
      </div>
    {/if}

    {#if phase === "awaiting-code"}
      <ol class="text-sm text-muted-foreground space-y-2 list-decimal list-inside">
        <li>In the CareLink tab that opened, sign in and solve the captcha.</li>
        <li>
          When you finish, the page won't visibly move — it's redirecting to a link your browser
          can't open. You need to copy that link. Open developer tools first:
          <span class="font-medium text-foreground">press <kbd class="px-1 rounded border bg-muted">F12</kbd></span>,
          go to the <span class="font-medium text-foreground">Network</span> tab, and tick
          <span class="font-medium text-foreground">Preserve log</span> before signing in.
        </li>
        <li>
          After signing in, find the request whose URL starts with:
          <div class="mt-1 rounded bg-muted px-2 py-1 font-mono text-xs break-all text-foreground">
            {REDIRECT_PREFIX}?code=…
          </div>
          Right-click it → <span class="font-medium text-foreground">Copy → Copy URL</span>.
        </li>
        <li>Paste the whole thing below — you don't need to trim it.</li>
      </ol>

      <div class="space-y-2">
        <Label for="carelink-code">Paste the copied link (or just the code)</Label>
        <Textarea
          id="carelink-code"
          bind:value={codeInput}
          rows={3}
          class="font-mono text-xs break-all"
          placeholder="{REDIRECT_PREFIX}?code=…&state=…"
          disabled={busy}
        />
        {#if codeInput.trim() && detectedCode}
          <p class="flex items-center gap-1 text-xs text-green-600">
            <CheckCircle2 class="h-3.5 w-3.5" /> Authorization code detected.
          </p>
        {:else if codeInput.trim()}
          <p class="text-xs text-destructive">
            No <code>code=</code> found in that text. Copy the full
            <code>{REDIRECT_PREFIX}?code=…</code> link from the Network tab.
          </p>
        {/if}
      </div>

      <div class="flex gap-2">
        <Button onclick={finishConnect} disabled={busy || !detectedCode}>
          {busy ? "Finishing…" : "Finish connecting"}
        </Button>
        <Button variant="ghost" onclick={reset} disabled={busy}>Start over</Button>
      </div>
    {/if}

    {#if phase === "done"}
      <div class="flex items-start gap-2 text-sm">
        <CheckCircle2 class="h-5 w-5 text-green-600 shrink-0" />
        <div>
          <p class="font-medium">CareLink connected.</p>
          <p class="text-muted-foreground">
            {connectedUsername
              ? `Signed in as ${connectedUsername}. `
              : ""}Your refresh token is stored — syncing will use it automatically.
          </p>
        </div>
      </div>
      <Button variant="outline" size="sm" onclick={reset}>Reconnect</Button>
    {/if}
  </CardContent>
</Card>
