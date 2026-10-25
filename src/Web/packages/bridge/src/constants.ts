/** `NotificationBase.Level` as the API sends it. */
export const AlarmLevel = {
  Warn: 1,
  Urgent: 2,
} as const;

/**
 * The AlarmHub events that carry the API's alert engine alarms, as distinct from the
 * legacy `alarm`, `urgent_alarm` and `clear_alarm` raised through v2 notifications.
 * The bridge relays them to the `/alarm` namespace only: the web app shows engine
 * alerts as in-app notifications instead.
 */
export const EngineAlarmEvents = {
  Alarm: "engine_alarm",
  UrgentAlarm: "engine_urgent_alarm",
  ClearAlarm: "engine_clear_alarm",
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
