use crate::probe::{selected_device, Capture, ComGuard, SampleKind, Selection};
use echosub_audio_core::{AudioFormat, AudioFrame, AudioIdentity, StreamNormalizer};
use std::{
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering},
        mpsc::{self, Receiver},
        Arc, Mutex,
    },
    thread::JoinHandle,
    time::{Duration, Instant},
};
use windows::core::Interface;
use windows::Win32::{
    Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT},
    Media::Audio::{
        IMMDeviceEnumerator, MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY,
        AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR, DEVICE_STATE_ACTIVE,
    },
    System::{
        Com::{CoCreateInstance, CLSCTX_ALL},
        Threading::WaitForSingleObject,
    },
};

pub const PACKET_SLOTS: usize = 8;
pub const MAX_PACKET_SAMPLES: usize = 9600;
pub const FRAME_QUEUE: usize = 32;
struct Packet {
    samples: [f32; MAX_PACKET_SAMPLES],
    len: usize,
    qpc: u64,
}
fn qpc_100ns() -> Result<u64, u8> {
    use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
    let (mut ticks, mut frequency) = (0, 0);
    unsafe {
        QueryPerformanceFrequency(&mut frequency).map_err(|_| 8)?;
        QueryPerformanceCounter(&mut ticks).map_err(|_| 8)?;
    }
    if ticks < 0 || frequency <= 0 {
        return Err(8);
    }
    u64::try_from(ticks as u128 * 10_000_000 / frequency as u128).map_err(|_| 8)
}
fn mapped_origin(origin: u64, anchor_qpc: u64, first_qpc: u64) -> Result<u64, u8> {
    let delta = first_qpc.checked_sub(anchor_qpc).ok_or(8)?;
    let samples = u64::try_from(delta as u128 * 16_000 / 10_000_000).map_err(|_| 8)?;
    origin.checked_add(samples).ok_or(8)
}
#[derive(Default)]
struct PacketClock {
    expected: Option<u64>,
    last_qpc: u64,
}
impl PacketClock {
    fn check(&self, frames: u32, flags: u32, position: u64, qpc: u64) -> Result<(), u8> {
        if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
            return Err(6);
        }
        if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 && self.expected.is_some() {
            return Err(7);
        }
        if self.expected.is_some_and(|p| p != position)
            || (self.last_qpc != 0 && qpc < self.last_qpc)
            || position.checked_add(frames as u64).is_none()
        {
            return Err(8);
        }
        Ok(())
    }
    fn commit(&mut self, position: u64, frames: u32, qpc: u64) {
        self.expected = position.checked_add(frames as u64);
        self.last_qpc = qpc;
    }
}
#[derive(Clone)]
pub struct Info {
    pub device_id: String,
    pub rate: u32,
    pub channels: u16,
    pub mask: u32,
}
#[derive(Default)]
struct Shared {
    stop: AtomicBool,
    phase: AtomicU8,
    error: AtomicU8,
    ready: AtomicBool,
    packets: AtomicU64,
    input_frames: AtomicU64,
    normalized_frames: AtomicU64,
    discontinuities: AtomicU64,
    first_qpc: AtomicU64,
    last_qpc: AtomicU64,
    info: Mutex<Option<Info>>,
}
pub struct Stats {
    pub phase: &'static str,
    pub packets: u64,
    pub input_frames: u64,
    pub normalized_frames: u64,
    pub discontinuities: u64,
    pub first_qpc_100ns: u64,
    pub last_qpc_100ns: u64,
}
pub struct CaptureOwner {
    shared: Arc<Shared>,
    pub frames: Receiver<AudioFrame>,
    threads: Vec<JoinHandle<()>>,
}
impl Shared {
    fn fail(&self, code: u8) {
        let _ = self
            .error
            .compare_exchange(0, code, Ordering::AcqRel, Ordering::Acquire);
        self.stop.store(true, Ordering::Release);
    }
}
impl CaptureOwner {
    /// Device selection is pinned for this run, even when opened as the default.
    pub fn start(device: Option<String>, identity: AudioIdentity, origin: u64) -> Self {
        let shared = Arc::new(Shared::default());
        // Pair caller's monotonic session origin with QPC before opening WASAPI.
        let anchor_qpc = qpc_100ns();
        let (free_tx, free_rx) = mpsc::sync_channel(PACKET_SLOTS);
        for _ in 0..PACKET_SLOTS {
            free_tx
                .send(Box::new(Packet {
                    samples: [0.; MAX_PACKET_SAMPLES],
                    len: 0,
                    qpc: 0,
                }))
                .unwrap();
        }
        let (packet_tx, packet_rx) = mpsc::sync_channel::<Box<Packet>>(PACKET_SLOTS);
        let (format_tx, format_rx) = mpsc::sync_channel(1);
        let (frame_tx, frames) = mpsc::sync_channel(FRAME_QUEUE);
        let native_shared = shared.clone();
        let capture = std::thread::spawn(move || {
            let s = &native_shared;
            let result = (|| -> Result<(), u8> {
                s.phase.store(1, Ordering::Release);
                let _com = ComGuard::new().map_err(|_| 1)?;
                s.phase.store(2, Ordering::Release);
                let enumerator: IMMDeviceEnumerator =
                    unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
                        .map_err(|_| 1)?;
                let selection = device
                    .map(Selection::Fixed)
                    .unwrap_or(Selection::FollowDefault);
                let endpoint = selected_device(&enumerator, &selection).map_err(|_| 2)?;
                let kind: windows::Win32::Media::Audio::IMMEndpoint =
                    endpoint.cast().map_err(|_| 2)?;
                if unsafe { kind.GetDataFlow() }.map_err(|_| 2)?
                    != windows::Win32::Media::Audio::eRender
                {
                    return Err(2);
                }
                if unsafe { endpoint.GetState() }.map_err(|_| 2)? != DEVICE_STATE_ACTIVE {
                    return Err(2);
                }
                s.phase.store(3, Ordering::Release);
                let active =
                    Capture::open_observed(&endpoint, |p| s.phase.store(p, Ordering::Release))
                        .map_err(|_| 3)?;
                let f = active.format;
                if !matches!(f.kind, SampleKind::Float)
                    || f.bits != 32
                    || f.block_align != f.channels * 4
                {
                    return Err(4);
                }
                let format = AudioFormat::new(
                    f.rate,
                    f.channels as usize,
                    if f.channel_mask == 0 {
                        None
                    } else {
                        Some(f.channel_mask)
                    },
                )
                .map_err(|_| 4)?;
                *s.info.lock().unwrap() = Some(Info {
                    device_id: active.device_id.clone(),
                    rate: f.rate,
                    channels: f.channels,
                    mask: f.channel_mask,
                });
                format_tx.send(format).map_err(|_| 5)?;
                s.ready.store(true, Ordering::Release);
                s.phase.store(4, Ordering::Release);
                let mut clock = PacketClock::default();
                let mut spare = None;
                let mut checked = Instant::now();
                while !s.stop.load(Ordering::Acquire) {
                    match unsafe { WaitForSingleObject(active.event.0, 20) } {
                        WAIT_OBJECT_0 => {}
                        WAIT_TIMEOUT => {}
                        _ => return Err(3),
                    }
                    if s.stop.load(Ordering::Acquire) {
                        break;
                    }
                    loop {
                        if s.stop.load(Ordering::Acquire) {
                            break;
                        }
                        if unsafe { active.reader.GetNextPacketSize() }.map_err(|_| 3)? == 0 {
                            break;
                        }
                        // Reserve before borrowing WASAPI memory; channels are used
                        // only outside the GetBuffer/ReleaseBuffer interval.
                        let mut packet =
                            spare.take().or_else(|| free_rx.try_recv().ok()).ok_or(10)?;
                        let (mut data, mut frames, mut flags, mut position, mut qpc) =
                            (std::ptr::null_mut(), 0, 0, 0, 0);
                        unsafe {
                            active.reader.GetBuffer(
                                &mut data,
                                &mut frames,
                                &mut flags,
                                Some(&mut position),
                                Some(&mut qpc),
                            )
                        }
                        .map_err(|_| 3)?;
                        // AUDCLNT_S_BUFFER_EMPTY leaves data/position unwritten.
                        if frames == 0 {
                            spare = Some(packet);
                            break;
                        }
                        // Always release WASAPI's borrowed memory before publishing or errors.
                        let copied = (|| -> Result<(), u8> {
                            if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
                                s.discontinuities.fetch_add(1, Ordering::Relaxed);
                            }
                            clock.check(frames, flags, position, qpc)?;
                            let len = frames as usize * f.channels as usize;
                            if frames == 0 || len > MAX_PACKET_SAMPLES {
                                return Err(9);
                            }
                            packet.len = len;
                            packet.qpc = qpc;
                            if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
                                packet.samples[..len].fill(0.);
                            } else {
                                if data.is_null() {
                                    return Err(3);
                                }
                                for (i, value) in packet.samples[..len].iter_mut().enumerate() {
                                    *value =
                                        unsafe { (data as *const f32).add(i).read_unaligned() };
                                }
                            }
                            Ok(())
                        })();
                        unsafe { active.reader.ReleaseBuffer(frames) }.map_err(|_| 3)?;
                        copied?;
                        clock.commit(position, frames, qpc);
                        if s.packets.fetch_add(1, Ordering::Relaxed) == 0 {
                            s.first_qpc.store(qpc, Ordering::Relaxed);
                        }
                        s.last_qpc.store(qpc, Ordering::Relaxed);
                        s.input_frames.fetch_add(frames as u64, Ordering::Relaxed);
                        packet_tx.try_send(packet).map_err(|_| 10)?;
                    }
                    if checked.elapsed() >= Duration::from_millis(250) {
                        if unsafe { endpoint.GetState() }.map_err(|_| 2)? != DEVICE_STATE_ACTIVE {
                            return Err(2);
                        }
                        if matches!(selection, Selection::FollowDefault) {
                            let current =
                                selected_device(&enumerator, &selection).map_err(|_| 2)?;
                            if crate::probe::device_id(&current).map_err(|_| 2)? != active.device_id
                            {
                                return Err(11);
                            }
                        }
                        checked = Instant::now();
                    }
                }
                Ok(()) // Capture::Drop stops audio; COM objects die before ComGuard.
            })();
            if let Err(code) = result {
                s.fail(code);
            } else if s.error.load(Ordering::Acquire) == 0 {
                s.phase.store(5, Ordering::Release);
            }
        });
        let processing_shared = shared.clone();
        let processing = std::thread::spawn(move || {
            let s = &processing_shared;
            let format = loop {
                if s.stop.load(Ordering::Acquire) {
                    return;
                }
                match format_rx.recv_timeout(Duration::from_millis(20)) {
                    Ok(f) => break f,
                    Err(mpsc::RecvTimeoutError::Timeout) => continue,
                    Err(_) => return,
                }
            };
            let mut normalize = None;
            while !s.stop.load(Ordering::Acquire) {
                let packet = match packet_rx.recv_timeout(Duration::from_millis(20)) {
                    Ok(p) => p,
                    Err(mpsc::RecvTimeoutError::Timeout) => continue,
                    Err(_) => break,
                };
                if s.stop.load(Ordering::Acquire) {
                    break;
                }
                if normalize.is_none() {
                    match anchor_qpc.and_then(|qpc| mapped_origin(origin, qpc, packet.qpc)) {
                        Ok(start) => {
                            normalize = Some(StreamNormalizer::new(format, identity, start))
                        }
                        Err(code) => {
                            s.fail(code);
                            break;
                        }
                    }
                }
                let batch = normalize
                    .as_mut()
                    .unwrap()
                    .push(&packet.samples[..packet.len]);
                if free_tx.try_send(packet).is_err() {
                    s.fail(5);
                    break;
                }
                let batch = match batch {
                    Ok(b) => b,
                    Err(_) => {
                        s.fail(12);
                        break;
                    }
                };
                for frame in batch.frames {
                    if s.stop.load(Ordering::Acquire) {
                        break;
                    }
                    if frame_tx.try_send(frame).is_err() {
                        s.fail(13);
                        break;
                    }
                    s.normalized_frames.fetch_add(1, Ordering::Relaxed);
                }
            }
            // Stop/fault intentionally discards filter/frame tail; no synthetic flush.
        });
        Self {
            shared,
            frames,
            threads: vec![capture, processing],
        }
    }
    pub fn ready(&self) -> bool {
        self.shared.ready.load(Ordering::Acquire)
    }
    pub fn info(&self) -> Option<Info> {
        self.shared.info.lock().unwrap().clone()
    }
    pub fn error(&self) -> Option<&'static str> {
        match self.shared.error.load(Ordering::Acquire) {
            0 => None,
            1 => Some("COM_INITIALIZATION"),
            2 => Some("DEVICE_UNAVAILABLE"),
            3 => Some("WASAPI_ERROR"),
            4 => Some("UNSUPPORTED_FORMAT"),
            5 => Some("PROCESSOR_UNAVAILABLE"),
            6 => Some("TIMESTAMP_ERROR"),
            7 => Some("DATA_DISCONTINUITY"),
            8 => Some("POSITION_GAP"),
            9 => Some("PACKET_TOO_LARGE"),
            10 => Some("PACKET_QUEUE_OVERFLOW"),
            11 => Some("DEFAULT_DEVICE_CHANGED"),
            12 => Some("INVALID_PCM"),
            13 => Some("NORMALIZED_QUEUE_OVERFLOW"),
            _ => Some("CAPTURE_ERROR"),
        }
    }
    pub fn stats(&self) -> Stats {
        let s = &self.shared;
        Stats {
            phase: match s.phase.load(Ordering::Acquire) {
                0 => "Created",
                1 => "InitializingCOM",
                2 => "ResolvingEndpoint",
                3 => "OpeningClient",
                4 => "Capturing",
                6 => "GetDeviceId",
                7 => "ActivateAudioClient",
                8 => "GetMixFormat",
                9 => "InitializeAudioClient",
                10 => "SetEventAndGetService",
                11 => "StartAudioClient",
                _ => "Exited",
            },
            packets: s.packets.load(Ordering::Relaxed),
            input_frames: s.input_frames.load(Ordering::Relaxed),
            normalized_frames: s.normalized_frames.load(Ordering::Relaxed),
            discontinuities: s.discontinuities.load(Ordering::Relaxed),
            first_qpc_100ns: s.first_qpc.load(Ordering::Relaxed),
            last_qpc_100ns: s.last_qpc.load(Ordering::Relaxed),
        }
    }
    pub fn request_stop(&self) {
        self.shared.stop.store(true, Ordering::Release);
    }
    pub fn finished(&self) -> bool {
        self.threads.iter().all(|t| t.is_finished())
    }
}
impl Drop for CaptureOwner {
    fn drop(&mut self) {
        self.request_stop();
        for t in self.threads.drain(..) {
            let _ = t.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn qpc_anchor_preserves_restart_gap_and_rejects_regression_or_overflow() {
        assert_eq!(mapped_origin(16000, 100_000_000, 105_000_000), Ok(24000));
        assert_eq!(mapped_origin(16000, 100, 99), Err(8));
        assert_eq!(mapped_origin(u64::MAX, 0, 10_000_000), Err(8));
        assert_eq!(mapped_origin(0, 0, 625), Ok(1));
    }
    #[test]
    fn first_discontinuity_can_anchor_but_later_glitch_is_a_fault() {
        let mut c = PacketClock::default();
        let flag = AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32;
        assert_eq!(c.check(480, flag, 123, 10000), Ok(()));
        c.commit(123, 480, 10000);
        assert_eq!(c.check(480, 0, 603, 11000), Ok(()));
        assert_eq!(c.check(480, flag, 603, 11000), Err(7));
    }
    #[test]
    fn position_gap_backwards_qpc_and_overflow_are_rejected() {
        let mut c = PacketClock::default();
        c.commit(0, 480, 10000);
        assert_eq!(c.check(480, 0, 481, 11000), Err(8));
        assert_eq!(c.check(480, 0, 480, 9999), Err(8));
        assert_eq!(PacketClock::default().check(1, 0, u64::MAX, 1), Err(8));
        assert_eq!(c.expected, Some(480));
    }
    #[test]
    fn timestamp_error_never_establishes_an_anchor() {
        let c = PacketClock::default();
        assert_eq!(
            c.check(480, AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32, 0, 10000),
            Err(6)
        );
        assert_eq!(c.expected, None);
    }
    #[test]
    fn packet_pool_exhaustion_is_nonblocking_and_recycling_restores_one_slot() {
        let (tx, rx) = mpsc::sync_channel(PACKET_SLOTS);
        for i in 0..PACKET_SLOTS {
            tx.try_send(i).unwrap();
        }
        let mut held = Vec::new();
        for _ in 0..PACKET_SLOTS {
            held.push(rx.try_recv().unwrap());
        }
        assert!(matches!(rx.try_recv(), Err(mpsc::TryRecvError::Empty)));
        tx.try_send(held.pop().unwrap()).unwrap();
        assert!(rx.try_recv().is_ok());
        assert_eq!(PACKET_SLOTS * MAX_PACKET_SAMPLES * 4, 307200);
    }
    #[test]
    fn overload_requests_stop_and_preserves_first_fault() {
        let s = Shared::default();
        s.fail(10);
        s.fail(13);
        assert!(s.stop.load(Ordering::Acquire));
        assert_eq!(s.error.load(Ordering::Acquire), 10);
    }
}
