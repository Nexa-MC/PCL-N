#!/usr/bin/env python3
"""Capture actual X11 pixels from the independent resource preview process."""
import argparse
import json
import os
import pathlib
import subprocess
import time

STAGES = ('list-initial', 'append-loading', 'appended', 'next-loading', 'failed',
          'retry-loading', 'retry-success', 'detail', 'back')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--theme', required=True, choices=('light', 'dark'))
    parser.add_argument('--display', default=None)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('provide the executable command after --')
    display = args.display or os.environ.get('DISPLAY')
    if not display:
        parser.error('run this helper inside xvfb-run or an existing X11 DISPLAY')
    os.environ['DISPLAY'] = display
    out = pathlib.Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    marker = out / 'resource-preview.marker.tsv'
    marker.unlink(missing_ok=True)
    for stage in STAGES:
        (out / (stage + '.capture-ok')).unlink(missing_ok=True)
    deadline = time.monotonic() + 105
    recorder = None
    process = None
    metadata = []
    geometry = None
    video = out / ('resource-continuous-' + args.theme + '.mkv')
    gif = out / ('resource-continuous-' + args.theme + '.gif')
    with (out / 'native-preview.log').open('wb') as native_log, (out / 'capture.log').open('wb') as capture_log:
        try:
            process = subprocess.Popen(command, stdout=native_log, stderr=subprocess.STDOUT)
            while len(metadata) < len(STAGES):
                if time.monotonic() > deadline:
                    raise TimeoutError('preview capture exceeded its deadline')
                if process.poll() is not None:
                    raise RuntimeError('preview exited before all capture barriers; see ' + str(out / 'native-preview.log'))
                if not marker.exists():
                    time.sleep(.03)
                    continue
                fields = marker.read_text(encoding='utf-8').strip().split('\t')
                if len(fields) != 6:
                    raise RuntimeError('invalid marker: ' + repr(fields))
                stage, x, y, width, height, theme = fields
                expected = STAGES[len(metadata)]
                if any(item['stage'] == stage for item in metadata):
                    time.sleep(.03)
                    continue
                if stage != expected or theme != args.theme:
                    raise RuntimeError('unexpected capture stage/theme: ' + repr(fields))
                coords = tuple(int(value) for value in (x, y, width, height))
                if coords[2] <= 0 or coords[3] <= 0 or coords[0] < 0 or coords[1] < 0:
                    raise RuntimeError('invalid native capture geometry: ' + repr(coords))
                if geometry is None:
                    geometry = coords
                    recorder = subprocess.Popen([
                        'ffmpeg', '-hide_banner', '-loglevel', 'warning', '-y',
                        '-f', 'x11grab', '-framerate', '10', '-video_size', f'{coords[2]}x{coords[3]}',
                        '-i', f'{display}+{coords[0]},{coords[1]}', '-an', '-c:v', 'ffv1', str(video)
                    ], stdin=subprocess.PIPE, stdout=capture_log, stderr=subprocess.STDOUT)
                elif coords != geometry:
                    raise RuntimeError('native window geometry changed during recording')
                time.sleep(.55)  # Allow a real compositor frame; not a functional test assertion.
                if recorder.poll() is not None:
                    raise RuntimeError('ffmpeg recorder stopped; see ' + str(out / 'capture.log'))
                image = out / (stage + '-' + args.theme + '.png')
                crop = f'{coords[2]}x{coords[3]}+{coords[0]}+{coords[1]}'
                subprocess.run(['import', '-window', 'root', '-crop', crop, '+repage', str(image)],
                               check=True, stdout=capture_log, stderr=subprocess.STDOUT, timeout=10)
                if image.stat().st_size < 256:
                    raise RuntimeError('captured image is unexpectedly small: ' + str(image))
                metadata.append({'stage': stage, 'theme': theme, 'image': image.name,
                                 'screen_pixels': coords, 'fixture_data': True})
                (out / (stage + '.capture-ok')).write_text('captured\n', encoding='utf-8')
                print('CAPTURED ' + str(image), flush=True)
            exit_code = process.wait(timeout=max(1, deadline-time.monotonic()))
            if exit_code:
                raise RuntimeError(f'preview exit code {exit_code}; see native-preview.log')
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            if recorder is not None and recorder.poll() is None:
                try:
                    recorder.stdin.write(b'q\n')
                    recorder.stdin.flush()
                    recorder.wait(timeout=10)
                except (BrokenPipeError, subprocess.TimeoutExpired):
                    recorder.terminate()
                    try:
                        recorder.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        recorder.kill()
                        recorder.wait(timeout=5)
            if recorder is not None and recorder.stdin is not None:
                recorder.stdin.close()
        if recorder is None or recorder.returncode:
            raise RuntimeError('recording did not exit cleanly; see capture.log')
        subprocess.run([
            'ffmpeg', '-hide_banner', '-loglevel', 'warning', '-y', '-i', str(video),
            '-filter_complex', '[0:v]fps=10,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3',
            '-loop', '-1', str(gif)
        ], check=True, stdout=capture_log, stderr=subprocess.STDOUT, timeout=40)
    (out / 'resource-preview.json').write_text(json.dumps({
        'capture': 'Actual X11 window pixels', 'input': 'Native-window routed wheel/click events',
        'data': 'Controlled typed fixture catalog; no live provider/download claim',
        'theme': args.theme, 'gif': gif.name, 'stages': metadata
    }, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (out / 'README.md').write_text(
        '# Resource page previews\n\nActual native-window screenshots and continuous-list GIF. '
        'The GIF plays once; replay is provided by the aggregate preview index. All resource records and provider failures are controlled typed fixture data; '
        'these artifacts do not prove live provider/network behavior. '
        'Wheel/click events use the actual routed Backend path, not physical-device/XTest input.\n\n'
        + f'[{args.theme} continuous list GIF]({gif.name})\n\n'
        + '\n'.join(f'- [{item["stage"]}]({item["image"]})' for item in metadata) + '\n', encoding='utf-8')
    print('GIF ' + str(gif), flush=True)

if __name__ == '__main__':
    main()
