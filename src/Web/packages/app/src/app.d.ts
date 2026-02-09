// See https://svelte.dev/docs/kit/types#app
// for information about these interfaces
import { ApiClient } from "$lib/api";


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
}

declare global {
	namespace App {
		interface Error {
			message: string;
			details?: string;
			errorId?: string;
		}
		interface Locals {
			apiClient: ApiClient;
			/**
			 * Current authenticated user, or null if not authenticated
			 */
			user: AuthUser | null;
			/**
			 * Whether the current request is authenticated
			 */
			isAuthenticated: boolean;
			/**
			 * Whether site security has been checked for this request
			 */
			siteSecurityChecked?: boolean;
			/**
			 * Whether site requires authentication (lockdown mode)
			 */
			requireAuthentication?: boolean;
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
		// interface PageState {}
		// interface Platform {}
	}
}

export {};
