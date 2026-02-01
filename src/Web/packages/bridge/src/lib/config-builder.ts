import type { BridgeConfig, CompleteBridgeConfig } from "../types.js";
import {
  EnvironmentVariables,
} from "../constants.js";

export function buildConfig(
  userConfig: Partial<BridgeConfig>,
): CompleteBridgeConfig {
  if (!userConfig.signalr?.hubUrl) {
    throw new Error("SignalR hubUrl must be provided in configuration");
  }

  return {
    signalr: {
      hubUrl: userConfig.signalr.hubUrl,
      alarmHubUrl: userConfig.signalr.alarmHubUrl,
      reconnectAttempts: userConfig.signalr?.reconnectAttempts || 10,
      reconnectDelay: userConfig.signalr?.reconnectDelay || 5000,
      maxReconnectDelay: userConfig.signalr?.maxReconnectDelay || 30000,
    },
    socketio: {
      cors: {
        origin:
          userConfig.socketio?.cors?.origin ||
          (process.env[EnvironmentVariables.CorsOrigins]
            ? process.env[EnvironmentVariables.CorsOrigins]!.split(",")
            : ["*"]),
        methods: userConfig.socketio?.cors?.methods || ["GET", "POST"],
        credentials:
          userConfig.socketio?.cors?.credentials !== undefined
            ? userConfig.socketio.cors.credentials
            : true,
      },
      transports: userConfig.socketio?.transports || ["websocket", "polling"],
      pingTimeout: userConfig.socketio?.pingTimeout || 60000,
      pingInterval: userConfig.socketio?.pingInterval || 25000,
    },
    logging: {
      level:
        userConfig.logging?.level ||
        process.env[EnvironmentVariables.LogLevel] ||
        "info",
      format:
        userConfig.logging?.format ||
        (process.env[EnvironmentVariables.NodeEnv] === "production"
          ? "json"
          : "simple"),
    },
    apiSecret: userConfig.apiSecret || process.env.API_SECRET || "",
  };
}
