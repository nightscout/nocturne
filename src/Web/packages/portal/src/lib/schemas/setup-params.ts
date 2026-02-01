import { z } from "zod";

/**
 * Unified schema for setup wizard URL params.
 *
 * IMPORTANT: All optional fields must use .nullable().default(null)
 * to ensure runed can detect all schema keys. Without explicit defaults,
 * Zod omits undefined fields from validation, causing runed's has() check
 * to fail and preventing URL updates when setting values.
 */
export const SetupParamsSchema = z.object({
    step: z.coerce.number().default(0),
    type: z.enum(["fresh", "migrate", "compatibility-proxy"]).nullable().default(null),
    // Database config
    useContainer: z.coerce.boolean().default(true),
    connectionString: z.string().nullable().default(null),
    // Optional services
    watchtower: z.coerce.boolean().default(true),
    includeDashboard: z.coerce.boolean().default(true),
    includeScalar: z.coerce.boolean().default(true),
    // Nightscout config (for migrate/proxy)
    nightscoutUrl: z.string().nullable().default(null),
    nightscoutApiSecret: z.string().nullable().default(null),
    enableDetailedLogging: z.coerce.boolean().default(false),
    // Migration-specific: MongoDB connection
    migrationMode: z.enum(["Api", "MongoDb"]).default("Api"),
    mongoConnectionString: z.string().nullable().default(null),
    mongoDatabaseName: z.string().nullable().default(null),
    // Connectors (comma-separated list)
    connectors: z.string().nullable().default(null),
});

export type SetupParams = z.infer<typeof SetupParamsSchema>;
export type SetupType = NonNullable<SetupParams["type"]>;

export const setupTypeLabels: Record<SetupType, string> = {
    fresh: "Fresh Install",
    migrate: "Migrate from Nightscout",
    "compatibility-proxy": "Compatibility Proxy",
};

export const setupTypeDescriptions: Record<SetupType, string> = {
    fresh: "Set up a new Nocturne instance from scratch",
    migrate: "Import your existing Nightscout data into Nocturne",
    "compatibility-proxy": "Run Nocturne alongside your existing Nightscout",
};
