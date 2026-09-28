//! The Frame Buffer (ADR 0006): a POSIX shared-memory object the Engine creates and names with
//! `--frame-buffer=<name>`. While the Game View is live, the render thread copies each finished frame
//! into it; the Mac App reads the latest one on each display refresh.
//!
//! Layout (little-endian; `Skua.MacOS/GameHost/FrameBuffer.cs` mirrors it):
//!
//!   0  u32 magic "SKFB"   4  u32 version   8  u32 format (1 = RGBA8)   12 u32 slot count (3)
//!   16 u32 max width      20 u32 max height   24 u32 slot bytes        28 u32 latest slot (NO_SLOT: none yet)
//!   32 u32 reading slot (NO_SLOT: none)       40 u64 frames published
//!   64 + 48 * i  slot i: u64 seq (odd while written) | u64 frame number | u64 write stamp (ns on
//!                CLOCK_UPTIME_RAW, the mach_absolute_time clock) | u32 width | u32 height | u32 stride
//!   256 + slot bytes * i  slot i's rows, RGBA8, opaque
//!
//! The writer takes the slot that is neither the latest nor the one being read, makes its seq odd,
//! writes the rows and the fields, makes the seq even, then publishes it as the latest. The reader
//! checks the seq again after copying (a seqlock), so a frame overwritten mid-copy is dropped.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering, fence};

pub const MAGIC: u32 = u32::from_le_bytes(*b"SKFB");
pub const VERSION: u32 = 1;
pub const FORMAT_RGBA8: u32 = 1;
pub const SLOTS: u32 = 3;
pub const HEADER_BYTES: usize = 256;
/// The Engine writes it as the latest and reading slots of an empty buffer.
#[cfg_attr(not(test), allow(dead_code))]
pub const NO_SLOT: u32 = u32::MAX;

// Header offsets.
const MAGIC_AT: usize = 0;
const VERSION_AT: usize = 4;
const FORMAT_AT: usize = 8;
const SLOTS_AT: usize = 12;
const MAX_WIDTH: usize = 16;
const MAX_HEIGHT: usize = 20;
const SLOT_BYTES: usize = 24;
const LATEST: usize = 28;
const READING: usize = 32;
const PUBLISHED: usize = 40;
const SLOT_DESCS: usize = 64;
const SLOT_DESC_BYTES: usize = 48;
// Within a slot descriptor.
const SEQ: usize = 0;
const NUMBER: usize = 8;
const STAMP: usize = 16;
const WIDTH: usize = 24;
const HEIGHT: usize = 28;
const STRIDE: usize = 32;

/// A mapped Frame Buffer. The Engine owns the object; this end only writes frames.
pub struct FrameBuffer {
    base: *mut u8,
    len: usize,
    max_width: u32,
    max_height: u32,
    slot_bytes: usize,
    /// Owned mappings are unmapped on drop; test buffers aren't.
    mapped: bool,
}

// SAFETY: the mapping lives as long as the struct; the header is only touched through atomics, and
// only one thread (the render thread) writes frames.
unsafe impl Send for FrameBuffer {}
unsafe impl Sync for FrameBuffer {}

impl FrameBuffer {
    /// Opens and maps the named object, and checks its header.
    pub fn open(name: &str) -> Result<FrameBuffer, String> {
        let cname = std::ffi::CString::new(name).map_err(|_| format!("bad Frame Buffer name {name:?}"))?;
        // SAFETY: plain libc calls on a NUL-terminated name and our own fd.
        unsafe {
            let fd = libc::shm_open(cname.as_ptr(), libc::O_RDWR);
            if fd < 0 {
                return Err(format!("shm_open {name}: {}", std::io::Error::last_os_error()));
            }
            let mut st: libc::stat = std::mem::zeroed();
            let len = if libc::fstat(fd, &mut st) == 0 {
                st.st_size as usize
            } else {
                0
            };
            if len < HEADER_BYTES {
                libc::close(fd);
                return Err(format!("{name} is {len} bytes, too small for a Frame Buffer"));
            }
            let base = libc::mmap(
                std::ptr::null_mut(),
                len,
                libc::PROT_READ | libc::PROT_WRITE,
                libc::MAP_SHARED,
                fd,
                0,
            );
            libc::close(fd);
            if base == libc::MAP_FAILED {
                return Err(format!("mmap {name}: {}", std::io::Error::last_os_error()));
            }
            Self::from_raw(base as *mut u8, len, true).inspect_err(|_| {
                libc::munmap(base, len);
            })
        }
    }

    /// # Safety
    /// `base` must point to `len` writable bytes, 8-aligned, that outlive the result.
    unsafe fn from_raw(base: *mut u8, len: usize, mapped: bool) -> Result<FrameBuffer, String> {
        let mut fb = FrameBuffer {
            base,
            len,
            max_width: 0,
            max_height: 0,
            slot_bytes: 0,
            mapped: false,
        };
        let (magic, version, format, slots) = (
            fb.u32(MAGIC_AT).load(Ordering::Acquire),
            fb.u32(VERSION_AT).load(Ordering::Relaxed),
            fb.u32(FORMAT_AT).load(Ordering::Relaxed),
            fb.u32(SLOTS_AT).load(Ordering::Relaxed),
        );
        if magic != MAGIC || version != VERSION || format != FORMAT_RGBA8 || slots != SLOTS {
            return Err(format!(
                "not a Frame Buffer this Game Host writes (magic {magic:#x}, version {version}, format {format}, slots {slots})"
            ));
        }
        fb.max_width = fb.u32(MAX_WIDTH).load(Ordering::Relaxed);
        fb.max_height = fb.u32(MAX_HEIGHT).load(Ordering::Relaxed);
        fb.slot_bytes = fb.u32(SLOT_BYTES).load(Ordering::Relaxed) as usize;
        if fb.slot_bytes < fb.max_width as usize * fb.max_height as usize * 4
            || HEADER_BYTES + SLOTS as usize * fb.slot_bytes > len
        {
            return Err(format!("Frame Buffer slots don't fit its {len} bytes"));
        }
        fb.mapped = mapped;
        Ok(fb)
    }

    fn u32(&self, at: usize) -> &AtomicU32 {
        debug_assert!(at + 4 <= HEADER_BYTES && at % 4 == 0);
        // SAFETY: in bounds and aligned (the mapping is page-aligned).
        unsafe { &*(self.base.add(at) as *const AtomicU32) }
    }

    fn u64(&self, at: usize) -> &AtomicU64 {
        debug_assert!(at + 8 <= HEADER_BYTES && at % 8 == 0);
        // SAFETY: as above.
        unsafe { &*(self.base.add(at) as *const AtomicU64) }
    }

    /// The largest frame the slots hold.
    pub fn max_size(&self) -> (u32, u32) {
        (self.max_width, self.max_height)
    }

    #[cfg(test)]
    fn published(&self) -> u64 {
        self.u64(PUBLISHED).load(Ordering::Relaxed)
    }

    /// Writes one frame (`height` rows of `width` RGBA8 pixels, `src_stride` bytes apart) into a free
    /// slot and publishes it. Returns false, writing nothing, if the frame is larger than the slots.
    pub fn write(&self, rgba: &[u8], width: u32, height: u32, src_stride: usize, stamp_ns: u64) -> bool {
        let row = width as usize * 4;
        if width == 0 || height == 0 || width > self.max_width || height > self.max_height || src_stride < row {
            return false;
        }
        if rgba.len() < (height as usize - 1) * src_stride + row {
            return false;
        }
        let latest = self.u32(LATEST).load(Ordering::Acquire);
        let reading = self.u32(READING).load(Ordering::Acquire);
        let slot = free_slot(latest, reading);
        let desc = SLOT_DESCS + SLOT_DESC_BYTES * slot as usize;
        let seq = self.u64(desc + SEQ);
        let s = seq.load(Ordering::Relaxed);
        seq.store(s | 1, Ordering::Relaxed);
        fence(Ordering::Release);

        // SAFETY: the slot's rows are inside the mapping (checked in `from_raw`), and `row * height`
        // fits a slot because width and height are within the maximum.
        let dst = unsafe {
            std::slice::from_raw_parts_mut(
                self.base.add(HEADER_BYTES + self.slot_bytes * slot as usize),
                row * height as usize,
            )
        };
        for (y, out) in dst.chunks_exact_mut(row).enumerate() {
            out.copy_from_slice(&rgba[y * src_stride..y * src_stride + row]);
        }
        let number = self.u64(PUBLISHED).load(Ordering::Relaxed) + 1;
        self.u64(desc + NUMBER).store(number, Ordering::Relaxed);
        self.u64(desc + STAMP).store(stamp_ns, Ordering::Relaxed);
        self.u32(desc + WIDTH).store(width, Ordering::Relaxed);
        self.u32(desc + HEIGHT).store(height, Ordering::Relaxed);
        self.u32(desc + STRIDE).store(row as u32, Ordering::Relaxed);
        seq.store((s | 1) + 1, Ordering::Release);
        self.u32(LATEST).store(slot, Ordering::Release);
        self.u64(PUBLISHED).store(number, Ordering::Release);
        true
    }
}

impl Drop for FrameBuffer {
    fn drop(&mut self) {
        if self.mapped {
            // SAFETY: our own mapping of `len` bytes.
            unsafe { libc::munmap(self.base as *mut libc::c_void, self.len) };
        }
    }
}

/// The slot to write: the lowest that is neither the latest frame nor the one being read.
pub fn free_slot(latest: u32, reading: u32) -> u32 {
    (0..SLOTS)
        .find(|&s| s != latest && s != reading)
        .expect("three slots leave one free")
}

/// Now on the write stamps' clock: nanoseconds of CLOCK_UPTIME_RAW, the mach_absolute_time clock.
pub fn now_ns() -> u64 {
    let mut ts = libc::timespec { tv_sec: 0, tv_nsec: 0 };
    // SAFETY: a plain libc call into our own timespec.
    unsafe { libc::clock_gettime(libc::CLOCK_UPTIME_RAW, &mut ts) };
    ts.tv_sec as u64 * 1_000_000_000 + ts.tv_nsec as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A heap Frame Buffer with the header the Engine writes.
    fn buffer(max_w: u32, max_h: u32) -> (Vec<u64>, FrameBuffer) {
        let slot_bytes = (max_w * max_h * 4) as usize;
        let len = HEADER_BYTES + 3 * slot_bytes;
        let mut mem = vec![0u64; len.div_ceil(8)];
        let base = mem.as_mut_ptr() as *mut u8;
        let header = [
            MAGIC,
            VERSION,
            FORMAT_RGBA8,
            SLOTS,
            max_w,
            max_h,
            slot_bytes as u32,
            NO_SLOT,
            NO_SLOT,
        ];
        for (i, v) in header.iter().enumerate() {
            unsafe { (base.add(i * 4) as *mut u32).write(*v) };
        }
        let fb = unsafe { FrameBuffer::from_raw(base, len, false) }.unwrap();
        (mem, fb)
    }

    fn bytes(mem: &[u64]) -> &[u8] {
        unsafe { std::slice::from_raw_parts(mem.as_ptr() as *const u8, mem.len() * 8) }
    }

    fn u32_at(mem: &[u64], at: usize) -> u32 {
        u32::from_le_bytes(bytes(mem)[at..at + 4].try_into().unwrap())
    }

    fn u64_at(mem: &[u64], at: usize) -> u64 {
        u64::from_le_bytes(bytes(mem)[at..at + 8].try_into().unwrap())
    }

    #[test]
    fn the_layout_matches_the_engine() {
        assert_eq!(MAGIC.to_le_bytes(), *b"SKFB");
        assert_eq!(SLOT_DESCS + SLOTS as usize * SLOT_DESC_BYTES, 208);
        assert!(SLOT_DESCS + SLOTS as usize * SLOT_DESC_BYTES <= HEADER_BYTES);
        assert_eq!(
            (MAX_WIDTH, MAX_HEIGHT, SLOT_BYTES, LATEST, READING, PUBLISHED),
            (16, 20, 24, 28, 32, 40)
        );
    }

    #[test]
    fn the_free_slot_is_neither_the_latest_nor_the_one_being_read() {
        assert_eq!(free_slot(NO_SLOT, NO_SLOT), 0);
        assert_eq!(free_slot(0, NO_SLOT), 1);
        assert_eq!(free_slot(1, 0), 2);
        assert_eq!(free_slot(2, 0), 1);
        assert_eq!(free_slot(0, 1), 2);
        for latest in 0..3 {
            for reading in 0..3 {
                let s = free_slot(latest, reading);
                assert!(s != latest && s != reading);
            }
        }
    }

    #[test]
    fn a_frame_is_published_with_its_rows_size_and_stamp() {
        let (mem, fb) = buffer(4, 3);
        // Two pixels a row, padded to 12 bytes like a wgpu readback.
        let src: Vec<u8> = (0..3 * 12).map(|i| i as u8).collect();
        assert!(fb.write(&src, 2, 3, 12, 777));

        assert_eq!(u32_at(&mem, LATEST), 0);
        assert_eq!(fb.published(), 1);
        let desc = SLOT_DESCS;
        assert_eq!(u64_at(&mem, desc), 2, "seq even after the write");
        assert_eq!(u64_at(&mem, desc + 8), 1, "frame number");
        assert_eq!(u64_at(&mem, desc + 16), 777);
        assert_eq!(
            (
                u32_at(&mem, desc + 24),
                u32_at(&mem, desc + 28),
                u32_at(&mem, desc + 32)
            ),
            (2, 3, 8)
        );
        let rows = &bytes(&mem)[HEADER_BYTES..HEADER_BYTES + 24];
        assert_eq!(&rows[..8], &src[0..8]);
        assert_eq!(&rows[8..16], &src[12..20]);
        assert_eq!(&rows[16..24], &src[24..32]);
    }

    #[test]
    fn frames_rotate_through_the_slots_around_the_reader() {
        let (mem, fb) = buffer(2, 2);
        let px = [9u8; 16];
        assert!(fb.write(&px, 2, 2, 8, 1));
        assert!(fb.write(&px, 2, 2, 8, 2));
        assert_eq!(u32_at(&mem, LATEST), 1);
        // The reader holds slot 0, so the next frame goes to slot 2, then back to 0 once it lets go.
        unsafe { ((mem.as_ptr() as *mut u8).add(READING) as *mut u32).write(0) };
        assert!(fb.write(&px, 2, 2, 8, 3));
        assert_eq!(u32_at(&mem, LATEST), 2);
        unsafe { ((mem.as_ptr() as *mut u8).add(READING) as *mut u32).write(NO_SLOT) };
        assert!(fb.write(&px, 2, 2, 8, 4));
        assert_eq!(u32_at(&mem, LATEST), 0);
        assert_eq!(fb.published(), 4);
        assert_eq!(u64_at(&mem, SLOT_DESCS + 8), 4);
        assert_eq!(u64_at(&mem, SLOT_DESCS), 4, "slot 0 written twice");
    }

    #[test]
    fn a_frame_larger_than_the_slots_is_not_written() {
        let (mem, fb) = buffer(2, 2);
        assert!(!fb.write(&[0; 64], 3, 2, 12, 1));
        assert!(!fb.write(&[0; 64], 2, 3, 8, 1));
        assert!(!fb.write(&[0; 4], 2, 2, 8, 1), "source too short");
        assert_eq!(u32_at(&mem, LATEST), NO_SLOT);
        assert_eq!(fb.published(), 0);
    }

    #[test]
    fn a_buffer_with_a_foreign_header_is_refused() {
        let mut mem = vec![0u64; HEADER_BYTES / 8];
        assert!(unsafe { FrameBuffer::from_raw(mem.as_mut_ptr() as *mut u8, HEADER_BYTES, false) }.is_err());
    }
}
