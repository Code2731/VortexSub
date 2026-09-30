use std::error::Error;
use std::ffi::c_void;
use std::time::{Duration, Instant};
use windows::core::PCWSTR;
use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::Media::Audio::{
    eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDevice, IMMDeviceEnumerator,
    MMDeviceEnumerator, AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY, AUDCLNT_BUFFERFLAGS_SILENT,
    AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR, AUDCLNT_SHAREMODE_SHARED,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK, AUDCLNT_STREAMFLAGS_LOOPBACK, DEVICE_STATE_ACTIVE,
    WAVEFORMATEX, WAVEFORMATEXTENSIBLE, WAVE_FORMAT_PCM,
};
use windows::Win32::System::Com::StructuredStorage::{PropVariantClear, PropVariantToStringAlloc};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
    COINIT_APARTMENTTHREADED, STGM_READ,
};
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};

type ProbeResult<T> = Result<T, Box<dyn Error>>;

#[derive(Clone)]
pub(crate) enum Selection {
    FollowDefault,
    Fixed(String),
}

struct Options {
    list: bool,
    duration: Duration,
    selection: Selection,
}

impl Options {
    fn parse() -> ProbeResult<Self> {
        let mut result = Self {
            list: false,
            duration: Duration::from_secs(600),
            selection: Selection::FollowDefault,
        };
        let mut args = std::env::args().skip(1);
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--list" => result.list = true,
                "--seconds" => {
                    let value = args.next().ok_or("--seconds needs a positive integer")?;
                    let seconds: u64 = value.parse()?;
                    if seconds == 0 {
                        return Err("--seconds must be positive".into());
                    }
                    result.duration = Duration::from_secs(seconds);
                }
                "--device-id" => {
                    result.selection =
                        Selection::Fixed(args.next().ok_or("--device-id needs an ID")?);
                }
                "--help" | "-h" => {
                    println!("Usage: echosub-capture-windows [--list] [--seconds N] [--device-id ID]\nDefault: follow the console render endpoint for 600 seconds. No PCM is saved.");
                    std::process::exit(0);
                }
                _ => return Err(format!("unknown argument: {arg}").into()),
            }
        }
        Ok(result)
    }
}

pub(crate) struct ComGuard;
impl ComGuard {
    pub(crate) fn new() -> ProbeResult<Self> {
        // Keep initial IAudioClient access in STA; native objects stay on this thread.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok()? };
        Ok(Self)
    }
}
impl Drop for ComGuard {
    fn drop(&mut self) {
        unsafe { CoUninitialize() }
    }
}

pub(crate) struct Event(pub(crate) HANDLE);
impl Drop for Event {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct Format {
    pub(crate) rate: u32,
    pub(crate) channels: u16,
    pub(crate) bits: u16,
    pub(crate) block_align: u16,
    pub(crate) channel_mask: u32,
    pub(crate) kind: SampleKind,
}

#[derive(Clone, Copy, Debug)]
pub(crate) enum SampleKind {
    Float,
    Pcm,
    Other,
}

impl Format {
    unsafe fn from_ptr(ptr: *const WAVEFORMATEX) -> Self {
        let raw = ptr.read_unaligned();
        let (kind, channel_mask) = if raw.wFormatTag == 0xfffe && raw.cbSize >= 22 {
            let ext = (ptr as *const WAVEFORMATEXTENSIBLE).read_unaligned();
            let subformat = ext.SubFormat;
            let code = subformat.to_u128();
            (
                match code {
                    0x00000003_0000_0010_8000_00aa00389b71 => SampleKind::Float,
                    0x00000001_0000_0010_8000_00aa00389b71 => SampleKind::Pcm,
                    _ => SampleKind::Other,
                },
                ext.dwChannelMask,
            )
        } else {
            (
                match raw.wFormatTag as u32 {
                    3 => SampleKind::Float,
                    WAVE_FORMAT_PCM => SampleKind::Pcm,
                    _ => SampleKind::Other,
                },
                0,
            )
        };
        Self {
            rate: raw.nSamplesPerSec,
            channels: raw.nChannels,
            bits: raw.wBitsPerSample,
            block_align: raw.nBlockAlign,
            channel_mask,
            kind,
        }
    }
}

#[derive(Default)]
struct Metrics {
    packets: u64,
    frames: u64,
    silent_packets: u64,
    discontinuities: u64,
    timestamp_errors: u64,
    wait_timeouts: u64,
    position_gaps: u64,
    missing_frames: u64,
    first_device_position: Option<u64>,
    last_device_position: Option<u64>,
    last_packet_frames: u32,
    first_qpc_100ns: Option<u64>,
    last_qpc_100ns: Option<u64>,
    level_peak: f64,
    energy_sum: f64,
    samples: u64,
}

impl Metrics {
    fn inspect(&mut self, data: &[u8], format: Format) {
        let bytes = (format.bits / 8) as usize;
        if bytes == 0 {
            return;
        }
        let stride = format.block_align as usize;
        let channels_bytes = format.channels as usize * bytes;
        if stride == 0 || channels_bytes > stride {
            return;
        }
        for sample in data
            .chunks_exact(stride)
            .flat_map(|frame| frame[..channels_bytes].chunks_exact(bytes))
        {
            let value = match (format.kind, format.bits) {
                (SampleKind::Float, 32) => f32::from_le_bytes(sample.try_into().unwrap()) as f64,
                (SampleKind::Pcm, 16) => {
                    i16::from_le_bytes(sample.try_into().unwrap()) as f64 / 32768.0
                }
                (SampleKind::Pcm, 24) => {
                    let signed = i32::from_le_bytes([
                        sample[0],
                        sample[1],
                        sample[2],
                        if sample[2] & 0x80 != 0 { 0xff } else { 0 },
                    ]);
                    signed as f64 / 8_388_608.0
                }
                (SampleKind::Pcm, 32) => {
                    i32::from_le_bytes(sample.try_into().unwrap()) as f64 / 2_147_483_648.0
                }
                _ => return,
            };
            if value.is_finite() {
                self.level_peak = self.level_peak.max(value.abs());
                self.energy_sum += value * value;
                self.samples += 1;
            }
        }
    }

    fn report(&mut self, elapsed: Duration, state: &str) {
        let rms = if self.samples == 0 {
            0.0
        } else {
            (self.energy_sum / self.samples as f64).sqrt()
        };
        println!("elapsed_s={:.1} state={state} packets={} frames={} silent={} discontinuities={} timestamp_errors={} wait_timeouts={} position_gaps={} missing_frames={} peak={:.6} rms={:.6} device_pos={:?}..{:?} qpc_100ns={:?}..{:?}", elapsed.as_secs_f64(), self.packets, self.frames, self.silent_packets, self.discontinuities, self.timestamp_errors, self.wait_timeouts, self.position_gaps, self.missing_frames, self.level_peak, rms, self.first_device_position, self.last_device_position, self.first_qpc_100ns, self.last_qpc_100ns);
        self.level_peak = 0.0;
        self.energy_sum = 0.0;
        self.samples = 0;
    }
}

pub(crate) struct Capture {
    pub(crate) device_id: String,
    pub(crate) client: IAudioClient,
    pub(crate) reader: IAudioCaptureClient,
    pub(crate) event: Event,
    pub(crate) format: Format,
}

impl Capture {
    pub(crate) fn open(device: &IMMDevice) -> ProbeResult<Self> {
        Self::open_observed(device, |_| {})
    }
    pub(crate) fn open_observed(device: &IMMDevice, phase: impl FnMut(u8)) -> ProbeResult<Self> {
        Self::open_observed_info(device, phase, |_, _| {})
    }
    pub(crate) fn open_observed_info(
        device: &IMMDevice,
        mut phase: impl FnMut(u8),
        mut opening_info: impl FnMut(&str, Format),
    ) -> ProbeResult<Self> {
        phase(6);
        let device_id = device_id(device)?;
        phase(7);
        let client: IAudioClient = unsafe { device.Activate(CLSCTX_ALL, None)? };
        phase(8);
        let mix = unsafe { client.GetMixFormat()? };
        let format = unsafe { Format::from_ptr(mix) };
        opening_info(&device_id, format);
        phase(9);
        let initialized = unsafe {
            client.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                // Shared event-driven streams require both durations to be zero.
                0,
                0,
                mix,
                None,
            )
        };
        unsafe { CoTaskMemFree(Some(mix as *const c_void)) };
        initialized?;
        phase(10);
        let event = Event(unsafe { CreateEventW(None, false, false, None)? });
        unsafe { client.SetEventHandle(event.0)? };
        let reader: IAudioCaptureClient = unsafe { client.GetService()? };
        phase(11);
        unsafe { client.Start()? };
        Ok(Self {
            device_id,
            client,
            reader,
            event,
            format,
        })
    }

    fn drain(&self, metrics: &mut Metrics) -> ProbeResult<()> {
        loop {
            let pending = unsafe { self.reader.GetNextPacketSize()? };
            if pending == 0 {
                break;
            }
            let mut data = std::ptr::null_mut();
            let mut frames = 0;
            let mut flags = 0;
            let mut position = 0;
            let mut qpc = 0;
            unsafe {
                self.reader.GetBuffer(
                    &mut data,
                    &mut frames,
                    &mut flags,
                    Some(&mut position),
                    Some(&mut qpc),
                )?
            };
            metrics.packets += 1;
            metrics.frames += u64::from(frames);
            if let Some(last) = metrics.last_device_position {
                let expected = last.saturating_add(u64::from(metrics.last_packet_frames));
                if position > expected {
                    metrics.position_gaps += 1;
                    metrics.missing_frames += position - expected;
                }
            }
            metrics.first_device_position.get_or_insert(position);
            metrics.last_device_position = Some(position);
            metrics.last_packet_frames = frames;
            metrics.first_qpc_100ns.get_or_insert(qpc);
            metrics.last_qpc_100ns = Some(qpc);
            if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
                metrics.silent_packets += 1;
            }
            if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
                metrics.discontinuities += 1;
            }
            if flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR.0 as u32 != 0 {
                metrics.timestamp_errors += 1;
            }
            if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 == 0 {
                let len = frames as usize * self.format.block_align as usize;
                if !data.is_null() && len > 0 {
                    metrics.inspect(
                        unsafe { std::slice::from_raw_parts(data, len) },
                        self.format,
                    );
                }
            }
            unsafe { self.reader.ReleaseBuffer(frames)? };
        }
        Ok(())
    }
}

impl Drop for Capture {
    fn drop(&mut self) {
        let _ = unsafe { self.client.Stop() };
    }
}

pub(crate) fn device_id(device: &IMMDevice) -> ProbeResult<String> {
    let ptr = unsafe { device.GetId()? };
    let id = unsafe { ptr.to_string() };
    unsafe { CoTaskMemFree(Some(ptr.0 as *const c_void)) };
    Ok(id?)
}

fn device_name(device: &IMMDevice) -> ProbeResult<String> {
    let properties = unsafe { device.OpenPropertyStore(STGM_READ)? };
    let mut value = unsafe { properties.GetValue(&PKEY_Device_FriendlyName)? };
    let name = (|| {
        let ptr = unsafe { PropVariantToStringAlloc(&value)? };
        let result = unsafe { ptr.to_string() };
        unsafe { CoTaskMemFree(Some(ptr.0 as *const c_void)) };
        Ok::<_, Box<dyn Error>>(result?)
    })();
    unsafe { PropVariantClear(&mut value)? };
    name
}

pub(crate) fn selected_device(
    enumerator: &IMMDeviceEnumerator,
    selection: &Selection,
) -> ProbeResult<IMMDevice> {
    Ok(match selection {
        Selection::FollowDefault => unsafe {
            enumerator.GetDefaultAudioEndpoint(eRender, eConsole)?
        },
        Selection::Fixed(id) => {
            let wide: Vec<u16> = id.encode_utf16().chain(std::iter::once(0)).collect();
            unsafe { enumerator.GetDevice(PCWSTR(wide.as_ptr()))? }
        }
    })
}

pub fn run() -> ProbeResult<()> {
    let options = Options::parse()?;
    let _com = ComGuard::new()?;
    let enumerator: IMMDeviceEnumerator =
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)? };
    if options.list {
        let devices = unsafe { enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)? };
        let default = selected_device(&enumerator, &Selection::FollowDefault)
            .ok()
            .and_then(|d| device_id(&d).ok());
        for index in 0..unsafe { devices.GetCount()? } {
            let device = unsafe { devices.Item(index)? };
            let id = device_id(&device)?;
            println!(
                "{} {} name={}",
                if default.as_deref() == Some(&id) {
                    "default"
                } else {
                    "render "
                },
                id,
                device_name(&device).unwrap_or_else(|_| "<unavailable>".to_owned())
            );
        }
        return Ok(());
    }

    let start = Instant::now();
    let mut last_report = start;
    let mut capture: Option<Capture> = None;
    let mut metrics = Metrics::default();
    while start.elapsed() < options.duration {
        let selected = selected_device(&enumerator, &options.selection);
        let state = match selected {
            Ok(device) if unsafe { device.GetState()? } == DEVICE_STATE_ACTIVE => {
                let id = device_id(&device)?;
                if capture.as_ref().is_some_and(|c| c.device_id != id) {
                    println!(
                        "transition=default_device_changed old={} new={id}",
                        capture.as_ref().unwrap().device_id
                    );
                    capture = None;
                }
                if capture.is_none() {
                    match Capture::open(&device) {
                        Ok(new_capture) => {
                            metrics.first_device_position = None;
                            metrics.last_device_position = None;
                            metrics.last_packet_frames = 0;
                            metrics.first_qpc_100ns = None;
                            metrics.last_qpc_100ns = None;
                            println!("transition=capturing device={} rate={} channels={} bits={} block_align={} mask=0x{:08x} sample={:?}", new_capture.device_id, new_capture.format.rate, new_capture.format.channels, new_capture.format.bits, new_capture.format.block_align, new_capture.format.channel_mask, new_capture.format.kind);
                            capture = Some(new_capture);
                        }
                        Err(error) => eprintln!("transition=open_failed device={id} error={error}"),
                    }
                }
                if capture.is_some() {
                    "capturing"
                } else {
                    "open_failed"
                }
            }
            Ok(_) => {
                if capture.take().is_some() {
                    println!("transition=device_inactive");
                }
                "device_inactive"
            }
            Err(error) => {
                if capture.take().is_some() {
                    println!("transition=device_unavailable error={error}");
                }
                "device_unavailable"
            }
        };
        if let Some(active) = &capture {
            match unsafe { WaitForSingleObject(active.event.0, 250) } {
                WAIT_OBJECT_0 => {
                    if let Err(error) = active.drain(&mut metrics) {
                        eprintln!(
                            "transition=capture_failed device={} error={error}",
                            active.device_id
                        );
                        capture = None;
                    }
                }
                WAIT_TIMEOUT => metrics.wait_timeouts += 1,
                other => return Err(format!("WaitForSingleObject failed: {}", other.0).into()),
            }
        } else {
            std::thread::sleep(Duration::from_millis(250));
        }
        if last_report.elapsed() >= Duration::from_secs(1) {
            metrics.report(start.elapsed(), state);
            last_report = Instant::now();
        }
    }
    drop(capture);
    metrics.report(start.elapsed(), "stopped");
    if metrics.packets == 0 {
        return Err("no loopback PCM packets received".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn format(kind: SampleKind, bits: u16) -> Format {
        Format {
            rate: 48_000,
            channels: 1,
            bits,
            block_align: bits / 8,
            channel_mask: 0,
            kind,
        }
    }

    #[test]
    fn float_level_ignores_nonfinite_values() {
        let mut metrics = Metrics::default();
        let mut samples = Vec::new();
        for value in [0.5_f32, -0.25, f32::NAN] {
            samples.extend_from_slice(&value.to_le_bytes());
        }
        metrics.inspect(&samples, format(SampleKind::Float, 32));
        assert_eq!(metrics.samples, 2);
        assert_eq!(metrics.level_peak, 0.5);
        assert!((metrics.energy_sum - 0.3125).abs() < 0.00001);
    }

    #[test]
    fn signed_pcm_24_level() {
        let mut metrics = Metrics::default();
        metrics.inspect(&[0, 0, 0x40, 0, 0, 0xc0], format(SampleKind::Pcm, 24));
        assert_eq!(metrics.samples, 2);
        assert_eq!(metrics.level_peak, 0.5);
    }
}
