use super::{Reply, Runtime};
use echosub_pipeline_core::{Record, SourceState};
use serde_json::{json, Value};
use std::{
    fmt::Write as _,
    io::Write as _,
    path::Path,
    time::{SystemTime, UNIX_EPOCH},
};

pub(super) fn utc_now() -> Result<String, (&'static str, &'static str)> {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| ("INTERNAL_ERROR", "UTC clock predates epoch"))?
        .as_millis();
    utc_from_millis(millis).ok_or(("INTERNAL_ERROR", "UTC clock out of range"))
}
fn utc_from_millis(millis: u128) -> Option<String> {
    if millis > 253_402_300_799_999 {
        return None;
    }
    let seconds = (millis / 1000) as u64;
    let mut days = seconds / 86400;
    let leap = |y: u64| y % 4 == 0 && (y % 100 != 0 || y % 400 == 0);
    let mut year = 1970;
    while days >= if leap(year) { 366 } else { 365 } {
        days -= if leap(year) { 366 } else { 365 };
        year += 1;
    }
    let months = [
        31,
        if leap(year) { 29 } else { 28 },
        31,
        30,
        31,
        30,
        31,
        31,
        30,
        31,
        30,
        31,
    ];
    let mut month = 0;
    while days >= months[month] {
        days -= months[month];
        month += 1;
    }
    let day_seconds = seconds % 86400;
    Some(format!(
        "{year:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z",
        month + 1,
        days + 1,
        day_seconds / 3600,
        day_seconds / 60 % 60,
        day_seconds % 60,
        millis % 1000
    ))
}
fn stamp(ms: u64) -> String {
    format!(
        "{:02}:{:02}:{:02},{:03}",
        ms / 3_600_000,
        ms / 60_000 % 60,
        ms / 1000 % 60,
        ms % 1000
    )
}
fn source_lines(source: &str) -> String {
    source
        .replace("\r\n", "\n")
        .replace('\r', "\n")
        .lines()
        .filter(|s| !s.trim().is_empty())
        .collect::<Vec<_>>()
        .join("\n")
}
fn render(
    uuid: &str,
    utc: &str,
    version: u64,
    origin: u64,
    records: &[Record],
    format: &str,
) -> (String, usize) {
    let mut text = String::new();
    let mut cues = 0;
    if format == "txt" {
        writeln!(text,"Session: {uuid}\nStarted UTC: {utc}\nHistory version: {version}\nRetained records: {} (maximum 1000; earlier records may have been evicted)\n",records.len()).unwrap();
    }
    for r in records {
        let start = r.range.start.saturating_sub(origin);
        let end = r.range.end.saturating_sub(origin);
        if format == "srt" {
            if r.source_state != SourceState::Final
                || r.applied_source_revision != Some(r.key.source_revision)
                || r.source.trim().is_empty()
                || start >= end
            {
                continue;
            }
            cues += 1;
            writeln!(
                text,
                "{cues}\n{} --> {}\n{}\n",
                stamp(start / 16),
                stamp(end.div_ceil(16)),
                source_lines(&r.source)
            )
            .unwrap();
        } else {
            writeln!(
                text,
                "[{:.3}..{:.3} seconds] epoch={} segment={} state={:?} reason={:?}\n{}\n",
                start as f64 / 16000.,
                end as f64 / 16000.,
                r.key.audio.epoch,
                r.key.segment_id,
                r.source_state,
                r.source_reason,
                r.source
            )
            .unwrap();
            if r.source_state == SourceState::Final {
                cues += 1;
            }
        }
    }
    (text, cues)
}
fn publish_file(
    path: &Path,
    text: &str,
    overwrite: bool,
) -> Result<(), (&'static str, &'static str)> {
    let parent = path
        .parent()
        .ok_or(("INVALID_REQUEST", "Export parent required"))?;
    if !parent.is_dir() {
        return Err(("INVALID_REQUEST", "Export parent does not exist"));
    }
    if !overwrite && path.exists() {
        return Err(("EXPORT_EXISTS", "Export file already exists"));
    }
    let temp = parent.join(format!(".echosub-{}.tmp", super::session::new_uuid()?));
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp)
        .map_err(|_| ("EXPORT_IO_ERROR", "Cannot create export staging file"))?;
    let written = file
        .write_all(text.as_bytes())
        .and_then(|_| file.sync_all());
    drop(file);
    if written.is_err() {
        let _ = std::fs::remove_file(&temp);
        return Err(("EXPORT_IO_ERROR", "Cannot write export"));
    }
    #[cfg(windows)]
    let published = {
        use std::os::windows::ffi::OsStrExt;
        use windows::{
            core::PCWSTR,
            Win32::Storage::FileSystem::{
                MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
            },
        };
        let from = temp
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let to = path
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let flags = if overwrite {
            MOVEFILE_WRITE_THROUGH | MOVEFILE_REPLACE_EXISTING
        } else {
            MOVEFILE_WRITE_THROUGH
        };
        unsafe { MoveFileExW(PCWSTR(from.as_ptr()), PCWSTR(to.as_ptr()), flags) }.is_ok()
    };
    #[cfg(not(windows))]
    let published = if overwrite {
        std::fs::rename(&temp, path).is_ok()
    } else {
        std::fs::hard_link(&temp, path).is_ok()
    };
    let _ = std::fs::remove_file(&temp);
    if published {
        Ok(())
    } else if !overwrite && path.exists() {
        Err(("EXPORT_EXISTS", "Export file already exists"))
    } else {
        Err(("EXPORT_IO_ERROR", "Cannot publish export"))
    }
}
impl Runtime {
    fn history_ready(&self) -> bool {
        matches!(self.session.state, "Idle" | "Paused")
            && self.capture.startable()
            && self.live_owner.is_none()
            && self.flight.is_none()
            && self.translation.is_none()
            && self.core.queue_lengths() == (0, 0, 0)
    }
    pub fn clear_history(&mut self, p: &Value, q: &crate::transport::Outbox) -> Reply {
        if !self.session.enabled {
            return Err(("UNSUPPORTED_CAPABILITY", "UUID session mode required"));
        }
        if !self.history_ready() {
            return Err((
                "INVALID_STATE",
                "Pause or stop and wait for all owners before clearing",
            ));
        }
        let uuid = p
            .get("session_id")
            .and_then(Value::as_str)
            .ok_or(("INVALID_REQUEST", "Session UUID required"))?;
        let (internal, _, _) = self
            .session
            .export_info(uuid)
            .ok_or(("STALE_SESSION", "Session history metadata not retained"))?;
        let removed = self
            .core
            .clear_session_history(internal)
            .map_err(super::core_error)?;
        self.session.cleared_history(internal);
        if removed != 0 {
            q.publish("history.changed", json!({"history_version":self.core.version(),"session_id":uuid,"reason":"history_cleared"}), None)
                .map_err(|_| ("INTERNAL_ERROR", "History event unavailable"))?;
        }
        Ok(json!({"session_id":uuid,"removed_count":removed,"history_version":self.core.version()}))
    }
    pub fn export_history(&self, p: &Value) -> Reply {
        if !self.session.enabled {
            return Err(("UNSUPPORTED_CAPABILITY", "UUID session mode required"));
        }
        if !self.history_ready() {
            return Err((
                "INVALID_STATE",
                "Pause or stop and wait for all owners before exporting",
            ));
        }
        let uuid = p
            .get("session_id")
            .and_then(Value::as_str)
            .ok_or(("INVALID_REQUEST", "Session UUID required"))?;
        let (internal, origin, utc) = self
            .session
            .export_info(uuid)
            .ok_or(("STALE_SESSION", "Session history metadata not retained"))?;
        let format = p
            .get("format")
            .and_then(Value::as_str)
            .filter(|s| matches!(*s, "txt" | "srt"))
            .ok_or(("INVALID_REQUEST", "Export format must be txt or srt"))?;
        let path = p
            .get("path")
            .and_then(Value::as_str)
            .filter(|s| !s.contains('\0') && s.len() <= 4096 && Path::new(s).is_absolute())
            .ok_or(("INVALID_REQUEST", "Absolute export path required"))?;
        let overwrite = p
            .get("overwrite")
            .and_then(Value::as_bool)
            .ok_or(("INVALID_REQUEST", "Explicit overwrite flag required"))?;
        let mut records = Vec::new();
        let mut offset = 0;
        loop {
            let page = self
                .core
                .history_page(self.core.version(), offset, 100)
                .map_err(super::core_error)?;
            records.extend(
                page.records
                    .into_iter()
                    .filter(|r| r.key.audio.session_id == internal),
            );
            if let Some(next) = page.next_offset {
                offset = next;
            } else {
                break;
            }
        }
        if records.is_empty() {
            return Err(("EMPTY_HISTORY", "No retained records for this session"));
        }
        let (text, cues) = render(uuid, utc, self.core.version(), origin, &records, format);
        if format == "srt" && cues == 0 {
            return Err(("EMPTY_HISTORY", "No final source cues"));
        }
        publish_file(Path::new(path), &text, overwrite)?;
        Ok(
            json!({"path":path,"cue_count":cues,"record_count":records.len(),"history_version":self.core.version(),"session_id":uuid,"format":format,"time_basis":"session_audio","source_only":true}),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn srt_excludes_non_final_and_rounds_sub_millisecond_range() {
        use echosub_audio_core::{AudioIdentity, JobIdentity, SampleRange};
        use echosub_pipeline_core::TranslationState;
        let final_record = Record {
            key: JobIdentity {
                audio: AudioIdentity {
                    session_id: 2,
                    epoch: 1,
                },
                segment_id: 1,
                source_revision: 1,
            },
            applied_source_revision: Some(1),
            range: SampleRange {
                start: 1001,
                end: 1002,
            },
            source_state: SourceState::Final,
            source: "한국어\r\n\r\n日本語".into(),
            source_reason: None,
            translation_state: TranslationState::None,
            translation: String::new(),
            translation_reason: None,
            translation_request_id: None,
            stable_source: String::new(),
            translation_is_preview: false,
            translation_source: String::new(),
            translation_prefix: String::new(),
        };
        let mut ignored = final_record.clone();
        ignored.source_state = SourceState::FinalPending;
        let (text, cues) = render("uuid", "UTC", 0, 1000, &[ignored, final_record], "srt");
        assert_eq!(cues, 1);
        assert_eq!(text, "1\n00:00:00,000 --> 00:00:00,001\n한국어\n日本語\n\n");
    }
    #[test]
    fn utc_calendar_handles_leap_years_and_bounds() {
        assert_eq!(utc_from_millis(0).unwrap(), "1970-01-01T00:00:00.000Z");
        assert_eq!(
            utc_from_millis(951_782_400_123).unwrap(),
            "2000-02-29T00:00:00.123Z"
        );
        assert_eq!(
            utc_from_millis(4_107_542_400_000).unwrap(),
            "2100-03-01T00:00:00.000Z"
        );
        assert_eq!(
            utc_from_millis(253_402_300_799_999).unwrap(),
            "9999-12-31T23:59:59.999Z"
        );
        assert!(utc_from_millis(253_402_300_800_000).is_none());
        assert_eq!(stamp(3_661_234), "01:01:01,234");
        assert_eq!(source_lines("한국어\r\n\r\n日本語"), "한국어\n日本語");
    }
}
