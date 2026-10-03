//! Deterministic seeding. Every random-looking quantity in a scene (paper
//! grain, granulation noise, brush jitter) derives from one [`Seed`] through
//! a splitmix64 stream, so a scene renders identically on every backend.

/// Root seed of a scene.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Seed(pub u64);

/// Purpose tag mixed into the root seed to derive independent sub-streams.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SubSeed {
    Paper,
    Granulation,
    /// Standing-water swirl noise (`swirl`).
    Swirl,
    /// Brush jitter for the timeline event with this index.
    Brush(u32),
}

impl SubSeed {
    fn tag(self) -> u64 {
        match self {
            SubSeed::Paper => 0x5041_5045_5200_0000,
            SubSeed::Granulation => 0x4752_414E_0000_0000,
            SubSeed::Swirl => 0x5357_4952_4C00_0000,
            SubSeed::Brush(i) => 0x4252_5553_4800_0000 ^ u64::from(i),
        }
    }
}

impl Seed {
    pub fn derive(self, purpose: SubSeed) -> Seed {
        Seed(mix64(
            self.0 ^ purpose.tag().wrapping_mul(0x9E37_79B9_7F4A_7C15),
        ))
    }

    pub fn stream(self) -> SeedStream {
        SeedStream { state: self.0 }
    }
}

/// splitmix64 finaliser.
pub fn mix64(mut z: u64) -> u64 {
    z = z.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

/// splitmix64 generator.
#[derive(Debug, Clone)]
pub struct SeedStream {
    state: u64,
}

impl SeedStream {
    pub fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// Uniform in `[0, 1)`.
    pub fn next_f32(&mut self) -> f32 {
        ((self.next_u64() >> 40) as f32) / ((1u64 << 24) as f32)
    }

    /// Uniform in `[-1, 1)`.
    pub fn next_signed(&mut self) -> f32 {
        self.next_f32() * 2.0 - 1.0
    }
}

/// Stateless 2-D integer hash to `[0, 1)`, shared by paper generation and the
/// shader port so both sample the same lattice values.
pub fn hash2(seed: u64, x: i32, y: i32) -> f32 {
    let k = seed ^ ((x as u32 as u64) << 32) ^ (y as u32 as u64).wrapping_mul(0x9E37_79B9);
    ((mix64(k) >> 40) as f32) / ((1u64 << 24) as f32)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sub_seeds_differ_by_purpose() {
        let s = Seed(7);
        assert_ne!(s.derive(SubSeed::Paper), s.derive(SubSeed::Granulation));
        assert_ne!(s.derive(SubSeed::Brush(0)), s.derive(SubSeed::Brush(1)));
        assert_eq!(s.derive(SubSeed::Paper), Seed(7).derive(SubSeed::Paper));
    }

    #[test]
    fn stream_is_in_unit_interval() {
        let mut st = Seed(1).stream();
        for _ in 0..1000 {
            let v = st.next_f32();
            assert!((0.0..1.0).contains(&v));
        }
    }
}
