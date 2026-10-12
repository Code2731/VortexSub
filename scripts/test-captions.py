"""One-shot caption regression gate. Uses restored .NET packages; downloads nothing."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--skip-render', action='store_true')
    parser.add_argument('--output-directory', type=Path)
    parser.add_argument('--baseline-report', type=Path)
    parser.add_argument('--replay-input', type=Path, action='append', default=[])
    args = parser.parse_args()
    if args.replay_input and (args.skip_render or sys.platform != 'win32'):
        parser.error('Recorded replay requires Windows saved-render checks')
    repo = Path(__file__).resolve().parent.parent
    output = (args.output_directory or repo / 'benchmarks/results' /
              ('caption-regression-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex[:8])).resolve()
    # Evidence is never overwritten, even on an accidentally repeated command.
    output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, NUGET_PACKAGES=str(repo / '.nuget/packages'),
               AVALONIA_TELEMETRY_OPTOUT='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    dotnet = shutil.which('dotnet')
    report = {
        'schema_version': 1, 'suite_version': 'caption-regression-v1',
        'started_at_utc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Fixed synthetic input, virtual clock, optional saved Avalonia frames. No live speech or translation quality verdict.',
        'suite_hash': hashlib.sha256((repo / 'apps/EchoSub.Desktop/CaptionRegression.cs').read_bytes()).hexdigest(),
        'platform': sys.platform, 'stages': [], 'passed': False,
        'render_selected': not args.skip_render and sys.platform == 'win32',
        'excluded': ['live audio', 'actual ASR/model inference', 'continuous desktop frames', 'human readability', 'macOS device acceptance'],
    }
    failures = []

    def save():
        (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

    def stage(name, command, timeout=120, expected_exit=0):
        log = output / (name + '.log')
        started = time.monotonic()
        timed_out = False
        error = None
        code = None
        with log.open('w', encoding='utf-8') as stream:
            try:
                process = subprocess.Popen(command, cwd=repo, env=env, stdout=stream, stderr=subprocess.STDOUT,
                    creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0)
                try:
                    code = process.wait(timeout=timeout)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    if sys.platform == 'win32':
                        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                                       stdout=stream, stderr=subprocess.STDOUT, timeout=15, check=False)
                    else:
                        process.kill()
                    process.wait(timeout=15)
            except Exception as exception:
                error = str(exception)
                stream.write(error + '\n')
        ok = not timed_out and error is None and code == expected_exit
        report['stages'].append(dict(name=name, passed=ok, exit_code=code, expected_exit=expected_exit,
                                     timed_out=timed_out, elapsed_s=round(time.monotonic() - started, 3),
                                     log=log.name, error=error))
        if not ok:
            failures.append(name)
        print(f"{name}: {'PASS' if ok else 'FAIL'} ({report['stages'][-1]['elapsed_s']:.3f} s)", flush=True)
        save()
        return ok

    def read_result(name):
        return json.loads((output / name).read_text(encoding='utf-8-sig'))

    try:
        if not dotnet:
            raise RuntimeError('dotnet is required. Use the development SDK and restored packages.')
        version = subprocess.run([dotnet, '--version'], capture_output=True, text=True, timeout=15, check=True)
        report['dotnet_sdk'] = version.stdout.strip()
        head = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=repo, capture_output=True, text=True, timeout=15, check=True)
        report['head'] = head.stdout.strip()
        report['source_hashes'] = {name: hashlib.sha256((repo / name).read_bytes()).hexdigest() for name in (
            'apps/EchoSub.Desktop/CaptionLines.cs', 'apps/EchoSub.Desktop/CaptionDeck.cs', 'apps/EchoSub.Desktop/OverlayWindow.cs',
            'apps/EchoSub.Desktop/CaptionScheduleStore.cs', 'tests/EchoSub.ProtocolSmoke/CaptionScheduleContractSmoke.cs',
            'apps/EchoSub.Desktop/CaptionSentenceReuse.cs', 'tests/EchoSub.ProtocolSmoke/CaptionSentenceReuseSmoke.cs',
            'apps/EchoSub.Desktop/CaptionScheduledLines.cs', 'tests/EchoSub.ProtocolSmoke/CaptionScheduledLinesSmoke.cs',
            'apps/EchoSub.Desktop/CaptionScheduledProbe.cs', 'apps/EchoSub.Desktop/CaptionScheduleDelivery.cs',
            'tests/EchoSub.ProtocolSmoke/CaptionScheduleDeliverySmoke.cs',
            'apps/EchoSub.Desktop/CaptionRegressionProbe.cs', 'apps/EchoSub.Desktop/CaptionDiagnostics.cs',
            'apps/EchoSub.Desktop/MainWindow.cs', 'apps/EchoSub.Desktop/App.cs',
            'scripts/summarize-caption-timing.py', 'tests/test_caption_timing_summary.py',
            'apps/EchoSub.Desktop/CaptionReplayProbe.cs', 'scripts/compare-caption-replay.py',
            'benchmarks/fixtures/caption-overlap-replay.json',
            'scripts/caption_replay_timing.py', 'tests/test_caption_replay_timing.py')}
        for name in ('apps/EchoSub.Desktop/OverlayPlacement.cs', 'tests/EchoSub.ProtocolSmoke/OverlayPlacementSmoke.cs'):
            report['source_hashes'][name] = hashlib.sha256((repo / name).read_bytes()).hexdigest()
        smoke = repo / 'tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj'
        build_ok = stage('build-state', [dotnet, 'build', str(smoke), '--no-restore'])
        if build_ok:
            binary = repo / 'tests/EchoSub.ProtocolSmoke/bin/Debug/net10.0/EchoSub.ProtocolSmoke.dll'
            stage('input-contract-checks', [dotnet, str(binary), '--caption-contract', str(output / 'contract.json')])
            if (output / 'contract.json').is_file():
                contract = read_result('contract.json')
                report['input_contract'] = contract
                if not contract.get('passed') or not contract.get('cases'):
                    failures.append('input-contract-report-verdict')
            else:
                failures.append('input-contract-report-missing')
            stage('existing-state-checks', [dotnet, str(binary), '--lines-only'])
            if stage('adapter-comparison', [dotnet, str(binary), '--caption-adapter-comparison', str(output / 'adapter-comparison.json')]):
                compared = read_result('adapter-comparison.json')
                valid = (compared.get('passed') is True and len(compared.get('cases', [])) == 16
                         and all(case['passed'] and not case['failures'] and case['early_removals'] == 0
                                 for case in compared['cases']))
                report['adapter_comparison'] = compared
                report['adapter_golden_compatible'] = valid
                report['adapter_default_ready'] = False  # Set only after integrated render validation.
                if not valid:
                    failures.append('adapter-comparison-golden-mismatch')
            stage('fixed-input-replay', [dotnet, str(binary), '--caption-regression', str(output / 'state.json')])
            if (output / 'state.json').is_file():
                state = read_result('state.json')
                if not state.get('passed'):
                    failures.append('state-report-verdict')
                report['state'] = state
                if not state.get('cases'):
                    failures.append('state-report-has-no-cases')
            else:
                failures.append('state-report-missing')
            if stage('negative-control', [dotnet, str(binary), '--caption-regression-negative-control', str(output / 'negative-control.json')], expected_exit=1):
                control = read_result('negative-control.json')
                # An arbitrary crash is not a successful regression detector.
                case = next((item for item in control.get('cases', []) if item['name'] == 'source-revision-at-0.064s'), {})
                detected = not control.get('passed') and any(item['rule'] == 'reading_deadline' for item in case.get('failures', []))
                report['negative_control_detected'] = detected
                if not detected:
                    failures.append('negative-control-did-not-detect-early-removal')
        stage('timing-summary-checks', [sys.executable, '-X', 'utf8', str(repo / 'tests/test_caption_timing_summary.py')])
        stage('replay-timing-checks', [sys.executable, '-X', 'utf8', str(repo / 'tests/test_caption_replay_timing.py')])
        if report['render_selected']:
            desktop = repo / 'apps/EchoSub.Desktop/EchoSub.Desktop.csproj'
            if stage('build-render', [dotnet, 'build', str(desktop), '--no-restore']):
                render_dir = output / 'render'
                render_dir.mkdir()
                binary = repo / 'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.dll'
                stage('render-replay', [dotnet, str(binary), '--caption-regression-report', str(render_dir / 'report.json')])
                candidate_dir = output / 'candidate-render'
                candidate_dir.mkdir()
                stage('candidate-render', [dotnet, str(binary), '--caption-schedule-report', str(candidate_dir / 'report.json')])
                if (candidate_dir / 'report.json').is_file():
                    candidate = json.loads((candidate_dir / 'report.json').read_text(encoding='utf-8-sig'))
                    report['candidate_render'] = candidate
                    if not candidate.get('passed') or not candidate.get('frames') or not candidate.get('checks'):
                        failures.append('candidate-render-verdict')
                    if stage('candidate-pixels', [sys.executable, '-X', 'utf8', str(repo / 'scripts/check-caption-replay-render.py'), str(candidate_dir / 'report.json')]):
                        pixels = json.loads((candidate_dir / 'pixel-report.json').read_text(encoding='utf-8'))
                        report['candidate_pixel_checks'] = len(pixels['checks'])
                        if not pixels.get('passed'):
                            failures.append('candidate-pixel-verdict')
                else:
                    failures.append('candidate-render-report-missing')
                adapter_dir = output / 'adapter-render'
                adapter_dir.mkdir()
                stage('adapter-render', [dotnet, str(binary), '--caption-adapter-report', str(adapter_dir / 'report.json')])
                if (adapter_dir / 'report.json').is_file():
                    adapter_render = json.loads((adapter_dir / 'report.json').read_text(encoding='utf-8-sig'))
                    report['adapter_render'] = adapter_render
                    if not adapter_render.get('passed') or not adapter_render.get('frames') or not adapter_render.get('checks'):
                        failures.append('adapter-render-verdict')
                    if stage('adapter-pixels', [sys.executable, '-X', 'utf8', str(repo / 'scripts/check-caption-replay-render.py'), str(adapter_dir / 'report.json')]):
                        pixels = json.loads((adapter_dir / 'pixel-report.json').read_text(encoding='utf-8'))
                        report['adapter_pixel_checks'] = len(pixels['checks'])
                        if not pixels.get('passed'):
                            failures.append('adapter-pixel-verdict')
                else:
                    failures.append('adapter-render-report-missing')
                live_dir = output / 'live-render'
                live_dir.mkdir()
                stage('live-render', [dotnet, str(binary), '--caption-live-regression-report', str(live_dir / 'report.json')])
                if (live_dir / 'report.json').is_file():
                    live = json.loads((live_dir / 'report.json').read_text(encoding='utf-8-sig'))
                    report['live_render'] = live
                    if not live.get('passed') or len((live.get('result') or {}).get('cases', [])) != 15 or not live.get('frames'):
                        failures.append('live-render-verdict')
                    if stage('live-pixels', [sys.executable, '-X', 'utf8', str(repo / 'scripts/check-caption-replay-render.py'), str(live_dir / 'report.json')]):
                        pixels = json.loads((live_dir / 'pixel-report.json').read_text(encoding='utf-8'))
                        report['live_pixel_checks'] = len(pixels['checks'])
                        if not pixels.get('passed'):
                            failures.append('live-pixel-verdict')
                    stage('live-timing-summary', [sys.executable, '-X', 'utf8', str(repo / 'scripts/summarize-caption-timing.py'), str(live_dir / 'timing.jsonl')])
                else:
                    failures.append('live-render-report-missing')
                if (render_dir / 'report.json').is_file():
                    rendered = json.loads((render_dir / 'report.json').read_text(encoding='utf-8-sig'))
                    report['render_result'] = rendered.get('result')
                    report['render_cases'] = len((rendered.get('result') or {}).get('cases', []))
                    report['render_frames'] = len(rendered['frames'])
                    if not rendered.get('passed') or not rendered['frames'] or not (rendered.get('result') or {}).get('passed'):
                        failures.append('render-report-verdict')
                    if rendered['frames'] and stage('render-pixels', [sys.executable, '-X', 'utf8', str(repo / 'scripts/check-caption-replay-render.py'), str(render_dir / 'report.json')]):
                        pixel = json.loads((render_dir / 'pixel-report.json').read_text(encoding='utf-8'))
                        report['pixel_checks'] = len(pixel['checks'])
                        if not pixel.get('passed'):
                            failures.append('pixel-report-verdict')
                else:
                    failures.append('render-report-missing')
        else:
            report['excluded'].append('render stage not selected')
        if args.baseline_report:
            baseline = json.loads(args.baseline_report.resolve().read_text(encoding='utf-8-sig'))
            compatible = baseline.get('suite_hash') == report['suite_hash'] and baseline.get('platform') == report['platform']
            report['baseline_compatible'] = compatible
            report['comparison'] = []
            if compatible and 'state' in baseline and 'state' in report:
                older = {case['name']: case for case in baseline['state']['cases']}
                for case in report['state']['cases']:
                    previous = older.get(case['name'])
                    if previous:
                        older_wait = previous['first_line_wait_max_seconds']
                        new_wait = case['first_line_wait_max_seconds']
                        regressed = (older_wait is not None and new_wait is not None and new_wait > older_wait + .100001)
                        if regressed:
                            failures.append('baseline-wait-regression:' + case['name'])
                        report['comparison'].append(dict(name=case['name'], previous_passed=previous['passed'], passed=case['passed'],
                            previous_wait_max_s=previous['first_line_wait_max_seconds'], wait_max_s=case['first_line_wait_max_seconds'],
                            previous_early_removals=previous['early_removals'], early_removals=case['early_removals'], regressed=regressed))
        report['recorded_comparisons'] = []
        replay_inputs = list(args.replay_input)
        if report['render_selected']:
            replay_inputs.insert(0, repo / 'benchmarks/fixtures/caption-overlap-replay.json')
        for index, trace in enumerate(replay_inputs):
            destination = output / ('recorded-' + str(index))
            if stage('recorded-comparison-' + str(index), [sys.executable, '-X', 'utf8',
                     str(repo / 'scripts/compare-caption-replay.py'), str(trace), str(destination)], timeout=420):
                compared = json.loads((destination / 'comparison.json').read_text(encoding='utf-8'))
                report['recorded_comparisons'].append(compared)
                if not compared.get('passed'):
                    failures.append('recorded-comparison-verdict-' + str(index))
        report['adapter_default_ready'] = bool(report.get('adapter_golden_compatible') and (report.get('live_render') or {}).get('passed') and not failures)
        report['passed'] = not failures
    except Exception as exception:
        failures.append(str(exception))
    report['failures'] = failures
    report['finished_at_utc'] = datetime.now(timezone.utc).isoformat()
    report['coverage'] = 'state and rendered snapshots' if report['render_selected'] else 'state only'
    save()
    lines = ['# Caption regression report', '', f"Selected checks: {'PASS' if report['passed'] else 'FAIL'}",
             f"Coverage: {report['coverage']}", '', report['scope'], '', '| Stage | Result | Seconds | Log |', '| --- | --- | ---: | --- |']
    for item in report['stages']:
        lines.append(f"| {item['name']} | {'PASS' if item['passed'] else 'FAIL'} | {item['elapsed_s']} | [{item['log']}]({item['log']}) |")
    lines.extend(['', 'Negative control intentionally fails the old early-removal policy. Its expected exit is 1.'])
    if 'input_contract' in report:
        contract = report['input_contract']
        lines.extend(['', '## Scheduling input contract', '',
                      f"Cases: {len(contract['cases'])}; checks: {contract['checks']}; passed: {contract['passed']}.",
                      'The live overlay uses the scheduled reader. Mock and legacy diagnostic paths remain separate.'])
        for case in contract['cases']:
            for failure in case['failures']:
                lines.append(f"\nContract failure in {case['name']}: {failure}")
    if 'adapter_comparison' in report:
        compared = report['adapter_comparison']
        lines.extend(['', '## Deck adapter comparison', '',
                      'Same existing 16 inputs; all must match the existing golden.',
                      'Expected comparison exit: 0. Live reader activation requires the integrated saved-frame gate.',
                      'Golden compatibility and the live-render verdict are separate checks. No real speech result is implied.', '',
                      '| Case | Existing golden compatible | Differences |', '| --- | --- | --- |'])
        for case in compared.get('cases', []):
            rules = ', '.join(sorted({item['rule'] for item in case['failures']}))
            lines.append(f"| {case['name']} | {case['passed']} | {rules} |")
    lines.extend(['', '## Fixed input cases', '',
                  '| Case | Result | Early removals | Max first-line wait (s) |', '| --- | --- | ---: | ---: |'])
    for case in report.get('state', {}).get('cases', []):
        lines.append(f"| {case['name']} | {'PASS' if case['passed'] else 'FAIL'} | {case['early_removals']} | {case['first_line_wait_max_seconds']} |")
        for failure in case['failures']:
            lines.append(f"\nFailure at {failure['at_seconds']} s: {failure['rule']} — {failure['detail']}")
    for case in (report.get('render_result') or {}).get('cases', []):
        for failure in case['failures']:
            lines.append(f"\nRender failure in {case['name']} at {failure['at_seconds']} s: {failure['rule']} — {failure['detail']}")
    if args.baseline_report:
        lines.extend(['', '## Baseline comparison', '', f"Compatible: {report.get('baseline_compatible', False)}",
                      'A different suite hash or platform cannot be compared.', '',
                      '| Case | Previous max wait (s) | Current max wait (s) | Regression |', '| --- | ---: | ---: | --- |'])
        for case in report.get('comparison', []):
            lines.append(f"| {case['name']} | {case['previous_wait_max_s']} | {case['wait_max_s']} | {case['regressed']} |")
    lines.extend(['', '## Excluded checks', '', *('- ' + item for item in report['excluded'])])
    if failures:
        lines.extend(['', '## Failures', '', *('- ' + item for item in failures)])
    (output / 'report.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print(f"Caption regression: {'PASS' if report['passed'] else 'FAIL'}; {output / 'report.md'}", flush=True)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
