// Inlined into the SSR head and run at parse time, before the bundle loads. The one deliberate raw
// fetch in the app: the realtime store's starting reads are consumed by baseFetch in api/client.ts.
(function () {
  try {
    if (typeof fetch !== "function") return;
    var now = Date.now();
    var oneDayAgo = new Date(now - 86400000).toISOString();
    var to = new Date(now).toISOString();
    var day = new Date(now);
    var midnight = new Date(day.getFullYear(), day.getMonth(), day.getDate()).getTime();
    var glucoseFrom = new Date(Math.min(midnight, now - 86400000)).toISOString();
    var enc = encodeURIComponent;
    var range = function (path, limit) {
      return path + "?from=" + enc(oneDayAgo) + "&to=" + enc(to) + "&limit=" + limit;
    };
    var urls = [
      "/api/v4/glucose/sensor?from=" + enc(glucoseFrom) + "&limit=1000",
      "/api/v4/profile/summary",
      "/api/v4/trackers/definitions",
      "/api/v4/trackers/instances",
      "/api/v4/notifications",
      range("/api/v4/insulin/boluses", 500),
      range("/api/v4/nutrition/carbs", 500),
      range("/api/v4/observations/bg-checks", 500),
      range("/api/v4/observations/notes", 500),
      range("/api/v4/observations/device-events", 500),
      range("/api/v4/device-status/aps", 50),
      "/api/v4/current-therapy-state",
    ];
    var responses = new Map();
    window.__nocturneInitialReads = { now: now, responses: responses };
    urls.forEach(function (url) {
      var response = fetch(url, { credentials: "include", headers: { Accept: "application/json" } });
      response.catch(function () {});
      responses.set(url, response);
    });
  } catch {
    window.__nocturneInitialReads = undefined;
  }
})();
