
import asyncio
import logging
import os
import sys
import threading
import time
from datetime import datetime, timezone
from typing import Optional

from fastapi import FastAPI
import uvicorn
from contextlib import asynccontextmanager

# Import tconnectsync
import tconnectsync
from tconnectsync.nightscout import NightscoutApi

# Setup logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("TConnectSync-Connector")

# Metrics tracking
class MetricsTracker:
    def __init__(self):
        self.total_entries = 0
        self.last_entry_time: Optional[datetime] = None
        self.last_sync_time: Optional[datetime] = None
        self._entries_buffer = [] # Store timestamps for 24h calculation
        self._lock = threading.Lock()

    def record_entry(self, timestamp: Optional[datetime] = None):
        with self._lock:
            self.total_entries += 1
            now = datetime.now(timezone.utc)
            self.last_sync_time = now

            if timestamp:
                if self.last_entry_time is None or timestamp > self.last_entry_time:
                    self.last_entry_time = timestamp
                self._entries_buffer.append(now)

    def get_metrics(self):
        with self._lock:
            now = datetime.now(timezone.utc)
            # Clean buffer (older than 24h)
            cutoff = now.timestamp() - 24 * 3600
            self._entries_buffer = [t for t in self._entries_buffer if t.timestamp() > cutoff]

            return {
                "totalEntries": self.total_entries,
                "lastEntryTime": self.last_entry_time.isoformat() if self.last_entry_time else None,
                "entriesLast24Hours": len(self._entries_buffer),
                "lastSyncTime": self.last_sync_time.isoformat() if self.last_sync_time else None
            }

metrics = MetricsTracker()

# Monkey-patch NightscoutApi.upload_entry to intercept data
original_upload_entry = NightscoutApi.upload_entry

def patched_upload_entry(self, ns_format, entity='treatments'):
    try:
        # Try to extract timestamp
        entry_time = None
        if 'created_at' in ns_format:
            try:
                # tconnectsync often uses ISO strings
                entry_time = datetime.fromisoformat(ns_format['created_at'].replace('Z', '+00:00'))
            except ValueError:
                pass

        metrics.record_entry(entry_time)
        logger.info(f"Intercepted upload for {entity}")
    except Exception as e:
        logger.error(f"Error tracking metrics: {e}")

    return original_upload_entry(self, ns_format, entity)

NightscoutApi.upload_entry = patched_upload_entry

# Background task wrapper
def run_tconnectsync():
    logger.info("Starting tconnectsync background loop...")
    try:
        # Ensure environment variables are set or defaults are used
        # tconnectsync expects env vars like TCONNECT_EMAIL, TCONNECT_PASSWORD
        # We pass --auto-update to run continuously

        # Check if credential env vars are present
        if not os.environ.get("TCONNECT_EMAIL") or not os.environ.get("TCONNECT_PASSWORD"):
            logger.warning("TCONNECT_EMAIL or TCONNECT_PASSWORD not set. Waiting for configuration...")
            # In a real scenario we might want to wait or exit. For now let tconnectsync fail gracefully or loop.

        # Add basic arguments
        args = ['--auto-update', '--check-login']

        # Optional: Add region if set
        if os.environ.get("TCONNECT_REGION"):
             args.extend(['--region', os.environ.get("TCONNECT_REGION")])

        tconnectsync.main(args)
    except Exception as e:
        logger.error(f"tconnectsync crashed: {e}")
        # Restart logic could go here, or let the container/process manager handle it.
        # Since we are in a thread, we might want to restart the thread or just log it.
        logger.info("tconnectsync thread exited.")


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup
    logger.info("Initializing connector...")

    # Start tconnectsync in a separate thread
    # We use a daemon thread so it dies when the main process dies
    t_thread = threading.Thread(target=run_tconnectsync, daemon=True)
    t_thread.start()

    yield

    # Shutdown
    logger.info("Shutting down connector...")

app = FastAPI(lifespan=lifespan)

@app.get("/health")
def health_check():
    return {"status": "healthy"}

@app.get("/health/data")
def health_data():
    m = metrics.get_metrics()
    config = {
        "syncIntervalMinutes": 1, # tconnectsync default is somewhat dynamic but usually short
        "connectSource": "TConnectSync (Python)"
    }

    return {
        "connectorName": "Tandem Connector",
        "status": "running",
        "metrics": m,
        "recentEntries": [], # Not implemented for now
        "configuration": config
    }

if __name__ == "__main__":
    port = int(os.environ.get("PORT", 8000))
    # Listen on all interfaces
    uvicorn.run(app, host="0.0.0.0", port=port)
