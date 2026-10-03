//! Offsets into the packed state and scratch buffers, in f32 elements. Must
//! match `shaders/common.wgsl`.

use nocturne_watercolour_core::domain::{CompositeMode, SimulationGrid};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StateLayout {
    pub n: usize,
    pub pigment_count: usize,
    pub width: usize,
    pub height: usize,
}

impl StateLayout {
    pub fn new(width: u32, height: u32, pigment_count: usize) -> Self {
        StateLayout {
            n: (width as usize) * (height as usize),
            pigment_count,
            width: width as usize,
            height: height as usize,
        }
    }

    pub fn wet(&self) -> usize {
        0
    }
    pub fn u(&self) -> usize {
        self.n
    }
    pub fn v(&self) -> usize {
        2 * self.n
    }
    pub fn p(&self) -> usize {
        3 * self.n
    }
    pub fn s(&self) -> usize {
        4 * self.n
    }
    pub fn c(&self) -> usize {
        5 * self.n
    }
    pub fn h(&self) -> usize {
        6 * self.n
    }
    pub fn m(&self) -> usize {
        7 * self.n
    }
    pub fn g(&self, k: usize) -> usize {
        (8 + k) * self.n
    }
    pub fn d(&self, k: usize) -> usize {
        (8 + self.pigment_count + k) * self.n
    }
    pub fn dry_rate(&self) -> usize {
        (8 + 2 * self.pigment_count) * self.n
    }
    /// `CompositeMode::flag()`, read by `render.wgsl`.
    pub fn composite_mode(&self) -> usize {
        self.dry_rate() + 1
    }
    /// `SimulationGrid::aspect`; CPU-side stamp geometry only, carried so
    /// `read_grid` round-trips.
    pub fn aspect(&self) -> usize {
        self.dry_rate() + 2
    }
    /// `SimulationGrid::settle_share`, read by `transfer.wgsl`.
    pub fn settle_share(&self) -> usize {
        self.dry_rate() + 3
    }
    /// `SimulationGrid::tick`, read by the swirl and advanced by `clock`.
    pub fn tick(&self) -> usize {
        self.dry_rate() + 4
    }
    /// `SimulationGrid::swirl_seed`, read by the swirl.
    pub fn swirl_seed(&self) -> usize {
        self.dry_rate() + 5
    }
    /// Total f32 count, including a 6-element header tail holding
    /// `dry_rate`, the composite-mode flag, the aspect, `settle_share`, the
    /// tick and the swirl seed. Both integers stay below `2^24`, so `f32`
    /// holds them exactly.
    pub fn state_len(&self) -> usize {
        self.dry_rate() + 6
    }
    pub fn state_bytes(&self) -> u64 {
        (self.state_len() * std::mem::size_of::<f32>()) as u64
    }

    pub fn scratch_u(&self) -> usize {
        0
    }
    pub fn scratch_v(&self) -> usize {
        self.n
    }
    pub fn scratch_q(&self) -> usize {
        3 * self.n
    }
    pub fn scratch_q2(&self) -> usize {
        4 * self.n
    }
    pub fn scratch_p(&self) -> usize {
        7 * self.n
    }
    pub fn scratch_s(&self) -> usize {
        8 * self.n
    }
    pub fn scratch_g(&self, k: usize) -> usize {
        (9 + k) * self.n
    }
    /// Cell corners, the size of the swirl's stream-function region.
    pub fn corner_count(&self) -> usize {
        (self.width + 1) * (self.height + 1)
    }
    pub fn scratch_psi(&self) -> usize {
        (9 + self.pigment_count) * self.n
    }
    pub fn scratch_len(&self) -> usize {
        self.scratch_psi() + self.corner_count()
    }

    /// Serialises a grid into the packed state layout.
    pub fn pack(&self, grid: &SimulationGrid) -> Vec<f32> {
        let mut out = vec![0.0f32; self.state_len()];
        let n = self.n;
        out[self.wet()..self.wet() + n].copy_from_slice(&grid.wet);
        out[self.u()..self.u() + n].copy_from_slice(&grid.velocity_u);
        out[self.v()..self.v() + n].copy_from_slice(&grid.velocity_v);
        out[self.p()..self.p() + n].copy_from_slice(&grid.pressure);
        out[self.s()..self.s() + n].copy_from_slice(&grid.saturation);
        out[self.c()..self.c() + n].copy_from_slice(&grid.capacity);
        out[self.h()..self.h() + n].copy_from_slice(&grid.paper_height);
        out[self.m()..self.m() + n].copy_from_slice(&grid.bleed_mask);
        let kn = self.pigment_count * n;
        out[self.g(0)..self.g(0) + kn].copy_from_slice(&grid.pigments_in_water);
        out[self.d(0)..self.d(0) + kn].copy_from_slice(&grid.pigments_deposited);
        out[self.dry_rate()] = grid.dry_rate;
        out[self.settle_share()] = grid.settle_share;
        out[self.composite_mode()] = grid.composite_mode.flag();
        out[self.aspect()] = grid.aspect;
        out[self.tick()] = grid.tick as f32;
        out[self.swirl_seed()] = grid.swirl_seed as f32;
        out
    }

    /// Inverse of [`pack`](Self::pack).
    pub fn unpack(&self, data: &[f32], width: u32, height: u32) -> SimulationGrid {
        let n = self.n;
        let kn = self.pigment_count * n;
        SimulationGrid {
            width,
            height,
            pigment_count: self.pigment_count,
            wet: data[self.wet()..self.wet() + n].to_vec(),
            velocity_u: data[self.u()..self.u() + n].to_vec(),
            velocity_v: data[self.v()..self.v() + n].to_vec(),
            pressure: data[self.p()..self.p() + n].to_vec(),
            pigments_in_water: data[self.g(0)..self.g(0) + kn].to_vec(),
            pigments_deposited: data[self.d(0)..self.d(0) + kn].to_vec(),
            saturation: data[self.s()..self.s() + n].to_vec(),
            capacity: data[self.c()..self.c() + n].to_vec(),
            paper_height: data[self.h()..self.h() + n].to_vec(),
            bleed_mask: data[self.m()..self.m() + n].to_vec(),
            dry_rate: data[self.dry_rate()],
            settle_share: data[self.settle_share()],
            composite_mode: CompositeMode::from_flag(data[self.composite_mode()]),
            aspect: data[self.aspect()],
            tick: data[self.tick()] as u32,
            swirl_seed: data[self.swirl_seed()] as u32,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nocturne_watercolour_core::domain::{Paper, PaperField, Seed};

    #[test]
    fn pack_unpack_round_trips() {
        let field = PaperField::generate(&Paper::rough(Seed(3)), 8, 6);
        let mut grid = SimulationGrid::new(&field, 3);
        grid.pressure[5] = 0.7;
        grid.pigments_in_water[2 * 48 + 7] = 0.3;
        grid.pigments_deposited[48 + 1] = 0.9;
        grid.dry_rate = 2.5;
        grid.composite_mode = CompositeMode::Luminous;
        grid.aspect = 4.0;
        grid.tick = 1234;
        grid.swirl_seed = 0x00AB_CDEF;
        let layout = StateLayout::new(8, 6, 3);
        let packed = layout.pack(&grid);
        assert_eq!(packed.len(), layout.state_len());
        assert_eq!(layout.unpack(&packed, 8, 6), grid);
    }
}
