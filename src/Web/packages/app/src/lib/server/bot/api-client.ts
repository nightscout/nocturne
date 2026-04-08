import type { BotApiClient } from "@nocturne/bot";
import type { ApiClient } from "$lib/api";

export function buildBotApiClient(api: ApiClient): BotApiClient {
  return {
    sensorGlucose: {
      async getAll(from, to, limit, offset, sort, device, source, signal) {
        return await api.sensorGlucose.getAll(from, to, limit, offset, sort, device, source, signal);
      },
    },
    alerts: {
      acknowledge: (request, signal) => api.alerts.acknowledge(request, signal),
      markDelivered: (deliveryId, request, signal) => api.alerts.markDelivered(deliveryId, request, signal),
      markFailed: (deliveryId, request, signal) => api.alerts.markFailed(deliveryId, request, signal),
      getPendingDeliveries: (channelType, signal) => api.alerts.getPendingDeliveries(channelType, signal),
    },
    chatIdentity: {
      resolve: (platform, platformUserId, signal) => api.chatIdentity.resolve(platform, platformUserId, signal),
      createLink: (request, signal) => api.chatIdentity.createLink(request, signal),
    },
    system: {
      heartbeat: (request, signal) => api.system.heartbeat(request, signal),
    },
  };
}
