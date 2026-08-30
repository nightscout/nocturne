// See https://svelte.dev/docs/kit/types#app
// for information about these interfaces
import { ApiClient, UserDisplayPreferences } from "$lib/api";


export interface ServerSettings {
	name: string;
	version: string;
	head: string;
	apiEnabled: boolean;
	runtimeState: string;
	settings: Record<string, unknown>;
	authorized?: Record<string, unknown>;
}

/**
 * Authenticated user information available in locals
 */
export interface AuthUser {
	subjectId: string;
	name: string;
	email?: string;
	roles: string[];
	permissions: string[];
	expiresAt?: Date;
	/** User's preferred language code (e.g., "en", "fr", "de") */
	preferredLanguage?: string;
	/** Per-user display preferences (units, time format, theme, chart style, etc.) */
	preferences?: UserDisplayPreferences;
	/** URL to the subject's avatar image */
	avatarUrl?: string;
}

/** The tenant status document, as returned by the API's status endpoint. */
type TenantStatusResponse = Awaited<ReturnType<ApiClient["status"]["getStatus"]>>;

declare global {
	namespace App {
		interface Error {
			message: string;
			details?: string;
			errorId?: string;
		}
		type TenantStatus = TenantStatusResponse;
		interface Locals {
			apiClient: ApiClient;
			/** Whether this request arrived on a public share host ({token}.share.{base-domain}). */
			isShareHost: boolean;
			/**
			 * Set-Cookie headers to append verbatim to the outgoing response, for the half of a
			 * same-named pair SvelteKit's cookie jar cannot hold. See propagateAuthCookies.
			 */
			rawSetCookies: string[];
			/**
			 * Memoized tenant status for this request. Read it through getRequestStatus rather
			 * than touching it directly.
			 */
			statusPromise?: Promise<TenantStatusResponse | null>;
			/**
			 * Current authenticated user, or null if not authenticated
			 */
			user: AuthUser | null;
			/**
			 * Whether the current request is authenticated
			 */
			isAuthenticated: boolean;
			/**
			 * Whether the API readiness probe has already run for this request
			 */
			statusProbed?: boolean;
			/**
			 * Effective permissions (granted scopes) for the current user on the current tenant
			 */
			effectivePermissions?: string[];
			/**
			 * Whether the current user is a platform administrator
			 */
			isPlatformAdmin: boolean;
			/**
			 * Whether the current session is a short-lived platform-admin access grant on a
			 * tenant the subject is not a member of (distinct from ordinary membership)
			 */
			isPlatformAccessGrant: boolean;
			/**
			 * Whether the current session is a guest link session (read-only, no subjectId)
			 */
			isGuestSession?: boolean;
			/**
			 * ISO datetime when the guest session expires (only set for guest sessions)
			 */
			guestExpiresAt?: string;
		}

		// Base page data interface for the main app
		interface BasePageData {
			loading: boolean;
			loadingMessage?: string;
			error?: string;
			serverSettings: ServerSettings | null;
			entries: Entry[];
			treatments: Treatment[];
			deviceStatus: DeviceStatus[];
			initialData?: {
				now: number;
				history: number;
				focusHours: number;
			};
		}

		// Main PageData interface that allows additional properties for reports
		interface PageData extends Partial<BasePageData> {
			[key: string]: any;
		}
		// Shallow-routing state. Dialogs key their browser-history entries here
		// (see useDialogHistory) so the back button can dismiss them.
		interface PageState {
			[key: string]: unknown;
		}
		// interface Platform {}
	}
}

export {};
