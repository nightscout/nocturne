<script lang="ts">
  import { page } from "$app/state";
  import { resetPasswordForm, getLocalAuthConfig } from "../auth.remote";
  import {
    CheckCircle,
    XCircle,
    Loader2,
    KeyRound,
    Eye,
    EyeOff,
  } from "lucide-svelte";

  // Get token from URL query parameter
  const token = $derived(page.url.searchParams.get("token") ?? "");

  // Get local auth config for password requirements
  const config = getLocalAuthConfig();

  // Password visibility toggles
  let showPassword = $state(false);
  let showConfirmPassword = $state(false);
</script>

<svelte:head>
  <title>Reset Password | Nocturne</title>
</svelte:head>

<svelte:boundary>
  {#snippet failed(error, reset)}
    <div
      class="min-h-screen flex items-center justify-center bg-gray-50 dark:bg-gray-900 py-12 px-4 sm:px-6 lg:px-8"
    >
      <div class="max-w-md w-full space-y-8 text-center">
        <div
          class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-red-100 dark:bg-red-900/30"
        >
          <XCircle class="w-10 h-10 text-red-600 dark:text-red-400" />
        </div>
        <h2
          class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
        >
          Something went wrong
        </h2>
        <p class="text-gray-600 dark:text-gray-400">
          We couldn't process your request. Please try again or contact support.
        </p>
        <p class="text-sm text-red-600 dark:text-red-400">
          {(error as Error).message}
        </p>
        <button
          onclick={reset}
          class="mt-4 text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 font-medium"
        >
          Try again
        </button>
      </div>
    </div>
  {/snippet}

  <div
    class="min-h-screen flex items-center justify-center bg-gray-50 dark:bg-gray-900 py-12 px-4 sm:px-6 lg:px-8"
  >
    <div class="max-w-md w-full space-y-8">
      {#if !token}
        <!-- No token provided -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-yellow-100 dark:bg-yellow-900/30"
          >
            <KeyRound class="w-10 h-10 text-yellow-600 dark:text-yellow-400" />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Reset Password
          </h2>
          <p class="mt-4 text-gray-600 dark:text-gray-400">
            No reset token was provided. Please check your email for the
            password reset link and click on it to continue.
          </p>
          <div class="mt-6 space-y-3">
            <a
              href="/auth/forgot-password"
              class="block text-indigo-600 hover:text-indigo-500 dark:text-indigo-400 font-medium"
            >
              Request a new reset link
            </a>
            <a
              href="/auth/login"
              class="block text-gray-600 hover:text-gray-500 dark:text-gray-400"
            >
              Return to login
            </a>
          </div>
        </div>
      {:else if resetPasswordForm.result?.success}
        <!-- Success -->
        <div class="text-center">
          <div
            class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
          >
            <CheckCircle class="w-10 h-10 text-green-600 dark:text-green-400" />
          </div>
          <h2
            class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
          >
            Password Reset!
          </h2>
          <p class="mt-4 text-gray-600 dark:text-gray-400">
            {resetPasswordForm.result.message}
          </p>
          <div class="mt-6">
            <a
              href="/auth/login"
              class="inline-flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500"
            >
              Log In
            </a>
          </div>
        </div>
      {:else}
        <!-- Reset password form -->
        <div>
          <div class="text-center">
            <div
              class="mx-auto w-16 h-16 flex items-center justify-center rounded-full bg-indigo-100 dark:bg-indigo-900/30"
            >
              <KeyRound
                class="w-10 h-10 text-indigo-600 dark:text-indigo-400"
              />
            </div>
            <h2
              class="mt-6 text-center text-3xl font-extrabold text-gray-900 dark:text-white"
            >
              Reset Your Password
            </h2>
            <p class="mt-2 text-sm text-gray-600 dark:text-gray-400">
              Enter your new password below.
            </p>
          </div>

          <form {...resetPasswordForm} class="mt-8 space-y-6">
            <input
              {...resetPasswordForm.fields.token.as("hidden", token)}
              value={token}
            />

            {#each resetPasswordForm.fields.allIssues() as issue}
              <div class="rounded-md bg-red-50 dark:bg-red-900/30 p-4">
                <p class="text-sm text-red-700 dark:text-red-300">
                  {issue.message}
                </p>
              </div>
            {/each}

            <div class="space-y-4">
              <!-- New Password -->
              <div>
                <label
                  for="password"
                  class="block text-sm font-medium text-gray-700 dark:text-gray-300"
                >
                  New Password
                </label>
                <div class="mt-1 relative">
                  <input
                    {...resetPasswordForm.fields._password.as("password")}
                    id="password"
                    type={showPassword ? "text" : "password"}
                    autocomplete="new-password"
                    required
                    class="appearance-none block w-full px-3 py-2 pr-10 border border-gray-300 dark:border-gray-600 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm dark:bg-gray-700 dark:text-white"
                    class:border-red-500={(
                      resetPasswordForm.fields._password.issues() ?? []
                    ).length > 0}
                    placeholder="Enter your new password"
                  />
                  <button
                    type="button"
                    onclick={() => (showPassword = !showPassword)}
                    class="absolute inset-y-0 right-0 pr-3 flex items-center text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                  >
                    {#if showPassword}
                      <EyeOff class="h-5 w-5" />
                    {:else}
                      <Eye class="h-5 w-5" />
                    {/if}
                  </button>
                </div>
                {#each resetPasswordForm.fields._password.issues() as issue}
                  <p class="mt-1 text-sm text-red-600 dark:text-red-400">
                    {issue.message}
                  </p>
                {/each}
                {#if resetPasswordForm.fields._password.issues()?.length === 0}
                  <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">
                    {#if config.loading}
                      Loading password requirements...
                    {:else if config.current}
                      Must be at least {config.current.passwordRequirements
                        ?.minLength ?? 12} characters
                    {:else}
                      Must be at least 12 characters
                    {/if}
                  </p>
                {/if}
              </div>

              <!-- Confirm Password -->
              <div>
                <label
                  for="confirmPassword"
                  class="block text-sm font-medium text-gray-700 dark:text-gray-300"
                >
                  Confirm Password
                </label>
                <div class="mt-1 relative">
                  <input
                    {...resetPasswordForm.fields.confirmPassword.as("password")}
                    id="confirmPassword"
                    type={showConfirmPassword ? "text" : "password"}
                    autocomplete="new-password"
                    required
                    class="appearance-none block w-full px-3 py-2 pr-10 border border-gray-300 dark:border-gray-600 rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-indigo-500 focus:border-indigo-500 sm:text-sm dark:bg-gray-700 dark:text-white"
                    class:border-red-500={(
                      resetPasswordForm.fields.confirmPassword.issues() ?? []
                    ).length > 0}
                    placeholder="Confirm your new password"
                  />
                  <button
                    type="button"
                    onclick={() => (showConfirmPassword = !showConfirmPassword)}
                    class="absolute inset-y-0 right-0 pr-3 flex items-center text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                  >
                    {#if showConfirmPassword}
                      <EyeOff class="h-5 w-5" />
                    {:else}
                      <Eye class="h-5 w-5" />
                    {/if}
                  </button>
                </div>
                {#each resetPasswordForm.fields.confirmPassword.issues() as issue}
                  <p class="mt-1 text-sm text-red-600 dark:text-red-400">
                    {issue.message}
                  </p>
                {/each}
              </div>
            </div>

            <div>
              <button
                type="submit"
                disabled={!!resetPasswordForm.pending}
                class="group relative w-full flex justify-center py-2 px-4 border border-transparent text-sm font-medium rounded-md text-white bg-indigo-600 hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-indigo-500 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {#if resetPasswordForm.pending}
                  <Loader2 class="w-5 h-5 animate-spin mr-2" />
                  Resetting...
                {:else}
                  Reset Password
                {/if}
              </button>
            </div>

            <div class="text-center">
              <a
                href="/auth/login"
                class="text-sm text-gray-600 hover:text-gray-500 dark:text-gray-400 dark:hover:text-gray-300"
              >
                Back to login
              </a>
            </div>
          </form>
        </div>
      {/if}
    </div>
  </div>
</svelte:boundary>
