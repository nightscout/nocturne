<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    HelpCircle,
    Fingerprint,
    Smartphone,
    KeyRound,
    Globe,
    ArrowLeft,
  } from "lucide-svelte";
  // Quote the sign-in controls by name — this is the page a locked-out person
  // reaches, so the names have to match what they're looking at.
  import { signInMethodLabels } from "$lib/components/auth/labels";
</script>

<svelte:head>
  <title>Login Help - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  <div class="w-full max-w-xl space-y-4">
    <Card.Root>
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <HelpCircle class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Login Help</Card.Title>
        <Card.Description>
          Learn about the different ways to sign in to Nocturne
        </Card.Description>
      </Card.Header>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <Fingerprint class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">Passkeys</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Passkeys are the primary way to sign in to Nocturne. They use your
          device's built-in security — like a fingerprint sensor, face
          recognition, or a hardware security key — so there's no password to
          remember.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          On the login page, tap
          <strong>{signInMethodLabels.passkey}</strong> and follow your device's
          prompt. If you have multiple accounts, choose
          <strong>{signInMethodLabels.username}</strong> and enter your username
          first.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <Smartphone class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">Authenticator App</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          An authenticator app (such as Google Authenticator or Authy) is a
          second step, not a way to sign in on its own. It shows a 6-digit code
          that refreshes every 30 seconds, and each code can be used once.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          If you've set one up, the login page asks for the code straight after
          your passkey. You can set up an authenticator in your account settings
          after signing in.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <KeyRound class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">Recovery Codes</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Recovery codes are one-time backup codes you received when you first
          set up your account. Use them if you can't access your passkey or
          authenticator app.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          On the login page, choose
          <strong>{signInMethodLabels.recoveryCode}</strong>, enter your username
          and one of your remaining codes. Each code can only be used once, so
          cross it off your list after use.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          A code doesn't sign you in on its own: it gives you ten minutes to set
          up a new passkey, and you sign in with that.
        </p>
      </Card.Content>
    </Card.Root>

    <Card.Root>
      <Card.Header>
        <div class="flex items-center gap-3">
          <div
            class="flex h-9 w-9 items-center justify-center rounded-full bg-primary/10"
          >
            <Globe class="h-5 w-5 text-primary" />
          </div>
          <Card.Title class="text-lg">External Providers</Card.Title>
        </div>
      </Card.Header>
      <Card.Content>
        <p class="text-sm leading-relaxed text-muted-foreground">
          Your Nocturne instance may allow you to sign in with an external
          account like Google, GitHub, or Microsoft. If available, you'll see
          provider buttons on the login page.
        </p>
        <p class="mt-2 text-sm leading-relaxed text-muted-foreground">
          External providers are configured by your instance administrator. If
          you don't see any provider buttons on the login page, this sign-in
          method isn't enabled for your instance.
        </p>
      </Card.Content>
    </Card.Root>

    <div class="flex justify-center">
      <Button href="/auth/login" variant="outline">
        <ArrowLeft class="mr-2 h-4 w-4" />
        Back to login
      </Button>
    </div>
  </div>
</div>
