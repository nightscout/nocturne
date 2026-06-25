<script lang="ts">
  import { invoke } from "@tauri-apps/api/core";
  import { listen } from "@tauri-apps/api/event";
  import { onMount } from "svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "@nocturne/ui/ui/card";
  import { Button } from "@nocturne/ui/ui/button";
  import { Textarea } from "@nocturne/ui/ui/textarea";
  import { Label } from "@nocturne/ui/ui/label";
  import { Alert, AlertDescription } from "@nocturne/ui/ui/alert";
  import {
    CheckCircle2,
    KeyRound,
    Link2,
    Loader2,
    Monitor,
    RotateCcw,
  } from "@lucide/svelte";
  import GlucoseCompanion from "$lib/GlucoseCompanion.svelte";

  type CommandError = { status?: number | null; message: string };
  type LinkInfo = { serverUrl: string };
  type CompleteResponse = {
    success: boolean;
    username?: string | null;
    country?: string | null;
  };

  type Phase = "link" | "ready" | "signing-in" | "completing" | "done";

  let phase = $state<Phase>("link");
  let linkCodeInput = $state("");
  let serverUrl = $state<string | null>(null);
  let region = $state<"EU" | "US">("EU");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let connectedUsername = $state<string | null>(null);

  function describeError(e: unknown): string {
    const err = e as CommandError;
    if (err?.status === 401) {
      return "The link code has expired. Generate a fresh one in Nocturne and link again.";
    }
    return err?.message ?? "Something went wrong.";
  }

  /** A 401 means the short-lived link token is dead — go back to the link step. */
  function handleApiError(e: unknown) {
    error = describeError(e);
    if ((e as CommandError)?.status === 401) {
      phase = "link";
      serverUrl = null;
    }
  }

  async function linkServer() {
    busy = true;
    error = null;
    try {
      const info = await invoke<LinkInfo>("link", { linkCode: linkCodeInput });
      serverUrl = info.serverUrl;
      phase = "ready";
      linkCodeInput = "";
    } catch (e) {
      error = describeError(e);
    } finally {
      busy = false;
    }
  }

  async function startConnect() {
    busy = true;
    error = null;
    try {
      await invoke("start_connect", { region });
      phase = "signing-in";
    } catch (e) {
      handleApiError(e);
    } finally {
      busy = false;
    }
  }

  async function completeConnect(code: string) {
    phase = "completing";
    error = null;
    try {
      const res = await invoke<CompleteResponse>("complete_connect", { code });
      if (!res.success) {
        error = "Sign-in could not be completed. Please try again.";
        phase = "ready";
        return;
      }
      connectedUsername = res.username ?? null;
      phase = "done";
    } catch (e) {
      const expired = (e as CommandError)?.status === 401;
      handleApiError(e);
      if (!expired) phase = "ready";
    }
  }

  async function cancelSignIn() {
    await invoke("cancel_login");
    phase = "ready";
  }

  function startOver() {
    phase = "link";
    serverUrl = null;
    error = null;
    connectedUsername = null;
  }

  onMount(() => {
    const unlistenCode = listen<string>("carelink-code", (event) => {
      completeConnect(event.payload);
    });
    const unlistenClosed = listen("carelink-login-closed", () => {
      // Fires after a capture too (we close the window) — only react while still waiting.
      if (phase === "signing-in") {
        phase = "ready";
        error = "The sign-in window was closed before finishing.";
      }
    });
    return () => {
      unlistenCode.then((fn) => fn());
      unlistenClosed.then((fn) => fn());
    };
  });
</script>

<main class="mx-auto flex min-h-screen max-w-md flex-col gap-6 p-6">
  <header class="flex items-center gap-3">
    <Monitor class="text-primary h-6 w-6" />
    <div>
      <h1 class="text-lg font-semibold">Nocturne Companion</h1>
      <p class="text-muted-foreground text-sm">Connect CareLink to your Nocturne site</p>
    </div>
  </header>

  {#if error}
    <Alert variant="destructive">
      <AlertDescription>{error}</AlertDescription>
    </Alert>
  {/if}

  {#if phase === "link"}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Link2 class="h-4 w-4" /> Link your Nocturne site
        </CardTitle>
        <CardDescription>
          In Nocturne, open Connectors → CareLink and choose “Generate link code”, then paste it
          here. The code is valid for 10 minutes.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-3">
        <Label for="link-code">Link code</Label>
        <Textarea
          id="link-code"
          bind:value={linkCodeInput}
          rows={3}
          class="font-mono text-xs break-all"
          placeholder="nocturne-connect://link?server=…&token=…"
          disabled={busy}
        />
        <Button onclick={linkServer} disabled={busy || !linkCodeInput.trim()}>
          {busy ? "Linking…" : "Link"}
        </Button>
      </CardContent>
    </Card>
  {/if}

  {#if phase === "ready"}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <KeyRound class="h-4 w-4" /> Connect your CareLink account
        </CardTitle>
        <CardDescription>
          A CareLink sign-in window will open. Sign in and solve the captcha there — this app
          captures the result automatically. Your password never leaves Medtronic's page.
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <p class="text-muted-foreground text-sm">
          Linked to <span class="text-foreground font-mono">{serverUrl}</span>
        </p>
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
          <p class="text-muted-foreground text-xs">
            Australia, NZ, Europe and most of the world use EU. Choose US only for a US CareLink
            account.
          </p>
        </div>
        <div class="flex items-center gap-2">
          <Button onclick={startConnect} disabled={busy}>
            {busy ? "Starting…" : "Connect CareLink"}
          </Button>
          <Button variant="ghost" size="sm" onclick={startOver}>Use a different site</Button>
        </div>
      </CardContent>
    </Card>
  {/if}

  {#if phase === "signing-in" || phase === "completing"}
    <Card>
      <CardContent class="flex flex-col items-center gap-3 py-8 text-center">
        <Loader2 class="text-primary h-6 w-6 animate-spin" />
        {#if phase === "signing-in"}
          <p class="text-sm">Waiting for you to sign in to CareLink…</p>
          <p class="text-muted-foreground text-xs">
            Finish signing in (and the captcha) in the CareLink window. The code is captured
            automatically when Medtronic redirects.
          </p>
          <Button variant="ghost" size="sm" onclick={cancelSignIn}>Cancel</Button>
        {:else}
          <p class="text-sm">Code captured — finishing the connection…</p>
        {/if}
      </CardContent>
    </Card>
  {/if}

  {#if phase === "done"}
    <Card>
      <CardContent class="space-y-4 pt-6">
        <div class="flex items-start gap-2">
          <CheckCircle2 class="mt-0.5 h-5 w-5 shrink-0 text-green-600" />
          <div class="text-sm">
            <p class="font-medium">CareLink connected.</p>
            <p class="text-muted-foreground">
              {connectedUsername ? `Signed in as ${connectedUsername}. ` : ""}Nocturne stored the
              refresh token and will sync automatically. You can close this app.
            </p>
          </div>
        </div>
        <Button variant="outline" size="sm" onclick={startOver}>
          <RotateCcw class="mr-1 h-3.5 w-3.5" /> Connect another account
        </Button>
      </CardContent>
    </Card>
  {/if}

  <GlucoseCompanion />
</main>
