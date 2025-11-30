import type { Handle } from "@sveltejs/kit";
import { ApiClient } from "$lib/api/api-client";
import type { HandleServerError } from "@sveltejs/kit";
import { PUBLIC_API_URL } from "$env/static/public";
import { env } from "$env/dynamic/private";
import { createHash } from "crypto";
import { sequence } from "@sveltejs/kit/hooks";

// Proxy handler for /api requests
const proxyHandle: Handle = async ({ event, resolve }) => {
  // Check if the request is for /api
  if (event.url.pathname.startsWith("/api")) {
    // Use NOCTURNE_API_URL for server-side (internal Docker network) if available,
    // otherwise fall back to PUBLIC_API_URL for development
    const apiBaseUrl = env.NOCTURNE_API_URL || PUBLIC_API_URL;
    if (!apiBaseUrl) {
      throw new Error(
        "Neither NOCTURNE_API_URL nor PUBLIC_API_URL is defined. Please set one in your environment variables."
      );
    }

    // Get the API secret and hash it with SHA1
    const apiSecret = env.API_SECRET;
    const hashedSecret = apiSecret
      ? createHash("sha1").update(apiSecret).digest("hex").toLowerCase()
      : null;

    // Construct the target URL
    const targetUrl = new URL(event.url.pathname + event.url.search, apiBaseUrl);

    // Forward the request to the backend API
    const headers = new Headers(event.request.headers);
    if (hashedSecret) {
      headers.set("api-secret", hashedSecret);
    }

    const proxyResponse = await fetch(targetUrl.toString(), {
      method: event.request.method,
      headers,
      body: event.request.method !== "GET" && event.request.method !== "HEAD"
        ? await event.request.arrayBuffer()
        : undefined,
    });

    // Return the proxied response
    return new Response(proxyResponse.body, {
      status: proxyResponse.status,
      statusText: proxyResponse.statusText,
      headers: proxyResponse.headers,
    });
  }

  return resolve(event);
};

const apiClientHandle: Handle = async ({ event, resolve }) => {
  // Use NOCTURNE_API_URL for server-side (internal Docker network) if available,
  // otherwise fall back to PUBLIC_API_URL for development
  const apiBaseUrl = env.NOCTURNE_API_URL || PUBLIC_API_URL;
  if (!apiBaseUrl) {
    throw new Error(
      "Neither NOCTURNE_API_URL nor PUBLIC_API_URL is defined. Please set one in your environment variables."
    );
  }

  // Get the API secret and hash it with SHA1
  const apiSecret = env.API_SECRET;
  const hashedSecret = apiSecret
    ? createHash("sha1").update(apiSecret).digest("hex").toLowerCase()
    : null;

  // Wrap SvelteKit's fetch to add authentication headers
  const httpClient = {
    fetch: async (url: RequestInfo, init?: RequestInit): Promise<Response> => {
      const headers = new Headers(init?.headers);

      // Add the hashed API secret as authentication
      if (hashedSecret) {
        headers.set("api-secret", hashedSecret);
      }

      return event.fetch(url, {
        ...init,
        headers,
      });
    },
  };

  event.locals.apiClient = new ApiClient(apiBaseUrl, httpClient);

  return resolve(event);
};

export const handleError: HandleServerError = async ({ error, event }) => {
  const errorId = crypto.randomUUID();
  console.error(`Error ID: ${errorId}`, error);
  console.log(
    `Error occurred during request: ${event.request.method} ${event.request.url}`
  );

  // Extract meaningful error message
  let message = "An unexpected error occurred";
  let details: string | undefined;

  if (error instanceof Error) {
    message = error.message;

    // Check for ApiException-style errors with response property
    const apiError = error as Error & { response?: string; status?: number };
    if (apiError.response) {
      try {
        const parsed = JSON.parse(apiError.response);
        details = parsed.error || parsed.message || apiError.response;
      } catch {
        details = apiError.response;
      }
    }
  } else if (typeof error === "string") {
    message = error;
  }

  return {
    message,
    details,
    errorId,
  };
};

// Chain the proxy handler before the API client handler
export const handle: Handle = sequence(proxyHandle, apiClientHandle);
