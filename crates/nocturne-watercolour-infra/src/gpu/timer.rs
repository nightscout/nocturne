//! GPU timestamps around a stretch of work, read back without ever blocking:
//! a sample is only started while the previous one is not in flight, and a
//! finished one is collected on a later call. Needs `TIMESTAMP_QUERY`; see
//! `GpuContext::has_timestamps`.

use std::cell::Cell;
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

const IDLE: u8 = 0;
const PENDING: u8 = 1;
const MAPPED: u8 = 2;
/// Operations per sample; each sample costs a query resolve and a map callback.
const SAMPLE_INTERVAL: u8 = 16;

pub(super) struct GpuTimer {
    set: wgpu::QuerySet,
    resolve: wgpu::Buffer,
    readback: wgpu::Buffer,
    state: Arc<AtomicU8>,
    sample_wait: Cell<u8>,
}

impl GpuTimer {
    pub(super) fn new(device: &wgpu::Device, label: &str) -> GpuTimer {
        let bytes = 2 * std::mem::size_of::<u64>() as u64;
        GpuTimer {
            set: device.create_query_set(&wgpu::QuerySetDescriptor {
                label: Some(label),
                ty: wgpu::QueryType::Timestamp,
                count: 2,
            }),
            resolve: device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size: bytes,
                usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: false,
            }),
            readback: device.create_buffer(&wgpu::BufferDescriptor {
                label: Some(label),
                size: bytes,
                usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                mapped_at_creation: false,
            }),
            state: Arc::new(AtomicU8::new(IDLE)),
            sample_wait: Cell::new(0),
        }
    }

    /// Counts one operation and says whether to sample it: the first once
    /// the last sample has been collected, then one in every
    /// [`SAMPLE_INTERVAL`]. An operation while a sample is in flight is not
    /// counted.
    pub(super) fn claim_sample(&self) -> bool {
        if self.state.load(Ordering::Acquire) != IDLE {
            return false;
        }
        let remaining = self.sample_wait.get();
        self.sample_wait.set(if remaining == 0 {
            SAMPLE_INTERVAL - 1
        } else {
            remaining - 1
        });
        remaining == 0
    }

    pub(super) fn compute_writes(
        &self,
        begin: bool,
        end: bool,
    ) -> wgpu::ComputePassTimestampWrites<'_> {
        wgpu::ComputePassTimestampWrites {
            query_set: &self.set,
            beginning_of_pass_write_index: begin.then_some(0),
            end_of_pass_write_index: end.then_some(1),
        }
    }

    pub(super) fn render_writes(
        &self,
        begin: bool,
        end: bool,
    ) -> wgpu::RenderPassTimestampWrites<'_> {
        wgpu::RenderPassTimestampWrites {
            query_set: &self.set,
            beginning_of_pass_write_index: begin.then_some(0),
            end_of_pass_write_index: end.then_some(1),
        }
    }

    /// Encodes the copy of both timestamps to the host; after the command
    /// buffer holding it is submitted, call [`request`](Self::request).
    pub(super) fn resolve(&self, enc: &mut wgpu::CommandEncoder) {
        enc.resolve_query_set(&self.set, 0..2, &self.resolve, 0);
        enc.copy_buffer_to_buffer(&self.resolve, 0, &self.readback, 0, self.resolve.size());
    }

    pub(super) fn request(&self) {
        self.state.store(PENDING, Ordering::Release);
        let state = Arc::clone(&self.state);
        self.readback
            .slice(..)
            .map_async(wgpu::MapMode::Read, move |result| {
                state.store(
                    if result.is_ok() { MAPPED } else { IDLE },
                    Ordering::Release,
                );
            });
    }

    /// Milliseconds between the two timestamps of a finished sample, once.
    pub(super) fn collect(&self, period_ns: f32) -> Option<f64> {
        if self.state.load(Ordering::Acquire) != MAPPED {
            return None;
        }
        let ticks = self.readback.slice(..).get_mapped_range().ok().map(|view| {
            let stamps: &[u64] = bytemuck::cast_slice(&view);
            stamps[1].saturating_sub(stamps[0])
        });
        self.readback.unmap();
        self.state.store(IDLE, Ordering::Release);
        Some(ticks? as f64 * f64::from(period_ns) / 1e6)
    }
}
