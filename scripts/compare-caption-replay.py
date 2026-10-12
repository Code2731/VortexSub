"""Compare restored native-history traces through both readers; no models or downloads."""
import argparse
from collections import Counter
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import sys
from caption_replay_timing import fragment_timings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    source = args.input.resolve()
    if source.stat().st_size > 32 * 1024 * 1024:
        raise ValueError('Replay input exceeds 32 MiB')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    binary = repo / 'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.dll'
    env = dict(os.environ, AVALONIA_TELEMETRY_OPTOUT='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    digest = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    trace = json.loads(source.read_text(encoding='utf-8'))
    results = {}
    for mode in ('legacy', 'scheduled'):
        directory = output / mode
        directory.mkdir()
        path = directory / 'report.json'
        command = ['dotnet', str(binary), '--caption-replay-input', str(source), '--caption-replay-report', str(path)]
        if mode == 'legacy':
            command.append('--caption-replay-legacy')
        with (directory / 'run.log').open('w', encoding='utf-8') as log:
            result = subprocess.run(command, cwd=repo, env=env, stdout=log, stderr=log, timeout=180)
        replay = json.loads(path.read_text(encoding='utf-8'))
        with (directory / 'pixels.log').open('w', encoding='utf-8') as log:
            pixels = subprocess.run([sys.executable, '-X', 'utf8', str(repo / 'scripts/check-caption-replay-render.py'), str(path)],
                                    cwd=repo, stdout=log, stderr=log, timeout=90)
        pixel_report = json.loads((directory / 'pixel-report.json').read_text(encoding='utf-8'))
        observations = [item['observation'] for item in replay['observations']]
        waits = sorted(item['WaitSeconds'] for item in observations if item['Kind'] == 'FirstLine')
        per_run = []
        for run in range(replay['runs']):
            events = [item['observation'] for item in replay['observations'] if item['run'] == run]
            values = sorted(item['WaitSeconds'] for item in events if item['Kind'] == 'FirstLine')
            frames = [frame for frame in replay['frames'] if frame['run'] == run]
            nonempty = [i for i, frame in enumerate(frames) if frame['upper'] or frame['lower']]
            drained = frames[nonempty[-1] + 1]['virtual_s'] if nonempty and nonempty[-1] + 1 < len(frames) else None
            receipts = {}
            for item in trace['runs'][run]['events']:
                record = item['message']['payload'].get('record', {})
                if record.get('translation_state') == 'Done' and record.get('translation'):
                    receipts.setdefault(record['segment_id'], item['at_s'])
            admission = {}
            for frame in frames:
                for slot in ('upper', 'lower'):
                    identity = frame.get(slot + '_identity')
                    if frame[slot] and identity and identity['Segment'] in receipts:
                        segment = identity['Segment']
                        admission.setdefault(segment, dict(segment=segment, at_s=frame['virtual_s'],
                            wait_s=frame['virtual_s'] - receipts[segment]))
            per_run.append(dict(run=run, first_lines=len(values), max_wait_s=max(values) if values else None,
                                first_visible_segments=list(admission.values()),
                                fragment_timings=fragment_timings(trace['runs'][run], frames),
                                final_drain_at_s=drained,
                                drops=sum(item['Kind'] == 'Dropped' for item in events)))
        results[mode] = dict(passed=result.returncode == 0 and pixels.returncode == 0 and replay['passed']
                            and pixel_report['passed'] and replay['input_sha256'] == digest,
                            runs=replay['runs'], checks=len(replay['checks']), frames=len(replay['frames']),
                            pixel_checks=len(pixel_report['checks']), observations=dict(Counter(item['Kind'] for item in observations)),
                            first_lines=len(waits), median_wait_s=statistics.median(waits) if waits else None,
                            p95_wait_s=waits[max(0, math.ceil(len(waits) * .95) - 1)] if waits else None,
                            max_wait_s=max(waits) if waits else None, per_run=per_run)
        print(mode, 'PASS' if results[mode]['passed'] else 'FAIL', flush=True)
    before, after = results['legacy'], results['scheduled']
    paired = [dict(run=a['run'], legacy_final_drain_at_s=a['final_drain_at_s'], scheduled_final_drain_at_s=b['final_drain_at_s'],
                   delta_s=b['final_drain_at_s'] - a['final_drain_at_s'])
              for a, b in zip(before['per_run'], after['per_run'])
              if a['final_drain_at_s'] is not None and b['final_drain_at_s'] is not None]
    visible = []
    fragments = []
    for a, b in zip(before['per_run'], after['per_run']):
        for left, right in zip(a['fragment_timings'], b['fragment_timings']):
            fragments.append(dict(run=a['run'], text=left['text'],
                segment=left['segment_id'], source_revision=left['source_revision'], request_id=left['translation_request_id'],
                legacy_status=left['status'], scheduled_status=right['status'],
                legacy_wait_s=left['wait_s'], scheduled_wait_s=right['wait_s'],
                delta_s=round(right['wait_s'] - left['wait_s'], 6)
                    if left['wait_s'] is not None and right['wait_s'] is not None else None))
        older = {item['segment']: item for item in a['first_visible_segments']}
        newer = {item['segment']: item for item in b['first_visible_segments']}
        for segment in sorted(older.keys() | newer.keys()):
            left, right = older.get(segment), newer.get(segment)
            visible.append(dict(run=a['run'], segment=segment,
                legacy_wait_s=left['wait_s'] if left else None, scheduled_wait_s=right['wait_s'] if right else None,
                delta_s=right['wait_s'] - left['wait_s'] if left and right else None))
    report = dict(passed=all(item['passed'] for item in results.values()) and before['runs'] == after['runs'],
                  scope='Same history events, 420 DIP overlay and virtual clock. Not fresh inference or natural-speech acceptance.',
                  input_scope=trace.get('scope', 'Recorded history; provenance must be checked in the input evidence.'),
                  input_sha256=digest, results=results, paired_run_final_drain=paired,
                  paired_first_visible_segments=visible,
                  paired_fragment_timings=fragments,
                  fragment_timing_scope='Exact single-row fragments tied to input identities. Prior visible/delivered text is not a new version render. Missing, wrapped or superseded fragments have no wait value. No semantic or rendering ownership inference.',
                  source_hashes={name: hashlib.sha256((repo / name).read_bytes()).hexdigest() for name in
                                 ('apps/EchoSub.Desktop/CaptionReplayProbe.cs', 'apps/EchoSub.Desktop/CaptionScheduledLines.cs',
                                  'apps/EchoSub.Desktop/CaptionScheduleStore.cs', 'apps/EchoSub.Desktop/CaptionSentenceReuse.cs',
                                  'apps/EchoSub.Desktop/CaptionLines.cs', 'apps/EchoSub.Desktop/CaptionDeck.cs',
                                  'apps/EchoSub.Desktop/OverlayWindow.cs', 'scripts/compare-caption-replay.py',
                                  'scripts/caption_replay_timing.py')},
                  note='PASS covers delivery, reading protection and pixels. FirstLine populations differ: legacy reports a unit, scheduled reports a version. Their percentiles are not a paired speed verdict. Final drain times are paired only for successful complete runs.')
    (output / 'comparison.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
