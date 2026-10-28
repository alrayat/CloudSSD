"""Preflight or publish a verified CloudSSD release; existing tags/assets are preserved."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default='alrayat/CloudSSD')
    parser.add_argument('--tag', required=True)
    parser.add_argument('--asset-dir', required=True, type=Path)
    parser.add_argument('--notes', required=True, type=Path)
    parser.add_argument('--publish', action='store_true', help='Upload to a draft, verify, then publish as Latest')
    args = parser.parse_args()
    if not re.fullmatch(r'v\d+\.\d+\.\d+', args.tag) or not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repo):
        raise SystemExit('Use a vMAJOR.MINOR.PATCH tag and an owner/repository name.')
    root = Path(__file__).resolve().parents[1]
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    version = args.tag[1:]
    archives = [args.asset_dir / f'CloudSSD-{version}-{kind}.zip' for kind in ('win-x64', 'source')]
    files = archives + [Path(str(p) + '.sha256') for p in archives] + [args.asset_dir / 'SHA256SUMS.txt']
    hashes = {}
    for path in files:
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(block)
        hashes[path.name] = digest.hexdigest()
    expected_lines = [f'{hashes[p.name]}  {p.name}' for p in archives]
    if args.asset_dir.joinpath('SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines() != expected_lines:
        raise SystemExit('SHA256SUMS.txt does not match the release archives.')
    for path, expected in zip(archives, expected_lines):
        if Path(str(path) + '.sha256').read_text(encoding='utf-8-sig').strip() != expected:
            raise SystemExit('Archive checksum sidecar does not match.')
        with zipfile.ZipFile(path) as archive:
            if archive.testzip():
                raise SystemExit('Archive integrity check failed.')
            if path == archives[1] and archive.comment.decode('ascii') != commit:
                raise SystemExit('Source ZIP was not exported from the current release commit.')
    notes = args.notes.read_text(encoding='utf-8-sig').strip()
    if not notes:
        raise SystemExit('Release notes must not be empty.')
    api = 'https://api.github.com/repos/' + args.repo
    filled = subprocess.run(['git', 'credential', 'fill'], cwd=root,
                            input=f'protocol=https\nhost=github.com\npath={args.repo}.git\n\n',
                            text=True, capture_output=True, env=dict(os.environ, GIT_TERMINAL_PROMPT='0'))
    credentials = dict(line.split('=', 1) for line in filled.stdout.splitlines() if '=' in line)
    token = credentials.get('password') if filled.returncode == 0 else None
    if not token:
        raise SystemExit('Existing Git authentication is unavailable. No release was changed.')

    def request(url, method='GET', payload=None, content_type='application/json', missing_ok=False):
        headers = {'Accept': 'application/vnd.github+json', 'Authorization': 'Bearer ' + token,
                   'User-Agent': 'CloudSSD-release-helper', 'X-GitHub-Api-Version': '2022-11-28'}
        data = None
        if payload is not None:
            data = payload if isinstance(payload, bytes) else json.dumps(payload).encode('utf-8')
            headers['Content-Type'] = content_type
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers, method=method), timeout=180) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if missing_ok and error.code == 404:
                return None
            raise SystemExit(f'GitHub {method} failed: HTTP {error.code}. Existing assets were preserved.') from None
        except (urllib.error.URLError, TimeoutError):
            raise SystemExit('GitHub network request failed. Inspect the draft before retrying; existing assets were preserved.') from None

    def tag_commit():
        ref = request(api + '/git/ref/tags/' + args.tag, missing_ok=True)
        if ref is None:
            return None
        obj = ref['object']
        for _ in range(4):
            if obj['type'] == 'commit':
                return obj['sha']
            if obj['type'] != 'tag':
                break
            obj = request(api + '/git/tags/' + obj['sha'])['object']
        raise SystemExit('Release tag does not resolve to a commit.')

    repository = request(api)
    if repository['full_name'].lower() != args.repo.lower():
        raise SystemExit('Repository identity mismatch.')
    if request(api + '/branches/main')['commit']['sha'] != commit:
        raise SystemExit('Push the reviewed source commit to main before publishing.')
    ref_commit = tag_commit()
    if ref_commit not in (None, commit):
        raise SystemExit('The existing tag points to another commit; it was preserved.')
    runs = request(api + '/actions/runs?' + urllib.parse.urlencode({'head_sha': commit, 'branch': 'main', 'per_page': 100}))
    checks = [r for r in runs['workflow_runs'] if r['name'] == 'CloudSSD checks' and r['event'] == 'push' and r['head_sha'] == commit]
    if not checks or checks[0]['status'] != 'completed' or checks[0]['conclusion'] != 'success':
        raise SystemExit('Successful CloudSSD CI is required for this commit; wait for the checks or fix them.')
    release = request(api + '/releases/tags/' + args.tag, missing_ok=True)
    if release is not None and not release['draft']:
        if release['name'] != f'CloudSSD {version}' or (release['body'] or '').strip() != notes:
            raise SystemExit('Published release metadata differs; existing release was preserved.')
    assets = {a['name']: a for a in release['assets']} if release else {}

    def matches(asset, path):
        return asset and asset.get('state') == 'uploaded' and asset['size'] == path.stat().st_size and asset.get('digest') == 'sha256:' + hashes[path.name]

    for path in files:
        if path.name in assets and not matches(assets[path.name], path):
            raise SystemExit('Existing asset differs and was preserved: ' + path.name)
    print(json.dumps({'repository': args.repo, 'commit': commit, 'tag': args.tag, 'ci': checks[0]['html_url'],
                      'assets': [{'name': p.name, 'size': p.stat().st_size, 'sha256': hashes[p.name]} for p in files]}), flush=True)
    if not args.publish:
        return
    if release is None:
        release = request(api + '/releases', 'POST', {'tag_name': args.tag, 'target_commitish': commit,
                          'name': f'CloudSSD {version}', 'body': notes, 'draft': True, 'prerelease': False})
        print('Created draft release.', flush=True)
    for path in files:
        if path.name in assets:
            continue
        if not release['draft']:
            raise SystemExit('Published release has incomplete assets; it was preserved.')
        print('Uploading ' + path.name, flush=True)
        url = release['upload_url'].split('{', 1)[0] + '?' + urllib.parse.urlencode({'name': path.name})
        asset = request(url, 'POST', path.read_bytes(), 'application/zip' if path.suffix == '.zip' else 'text/plain')
        if not matches(asset, path):
            raise SystemExit('Upload digest verification failed; release remains draft.')
    verified = request(api + '/releases/' + str(release['id']))
    uploaded = {a['name']: a for a in verified['assets']}
    if any(not matches(uploaded.get(p.name), p) for p in files):
        raise SystemExit('Final asset verification failed; release remains draft.')
    if verified['draft']:
        request(api + '/releases/' + str(release['id']), 'PATCH', {'name': f'CloudSSD {version}', 'body': notes,
                'draft': False, 'prerelease': False, 'make_latest': 'true'})
    published = request(api + '/releases/tags/' + args.tag)
    latest = request(api + '/releases/latest')
    if published['draft'] or published['prerelease'] or latest['id'] != published['id'] or tag_commit() != commit:
        raise SystemExit('Published tag, status or Latest verification failed.')
    print(json.dumps({'release': published['html_url'], 'latest': latest['tag_name'], 'commit': commit,
                      'assets': [{'name': a['name'], 'url': a['browser_download_url'], 'digest': a.get('digest')} for a in published['assets']]}), flush=True)


if __name__ == '__main__':
    main()
