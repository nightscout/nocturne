/** `NotificationBase.Level` as the API sends it. */
export const AlarmLevel = {
  Warn: 1,
  Urgent: 2,
} as const;

/**
 * Environment variable names for the WebSocket Bridge
 */
export const EnvironmentVariables = {
  SocketIoPort: "SOCKETIO_PORT",
  HealthPort: "HEALTH_PORT",
  CorsOrigins: "CORS_ORIGINS",
  LogLevel: "LOG_LEVEL",
  NodeEnv: "NODE_ENV",
} as const;
