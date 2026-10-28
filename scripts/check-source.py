"""Check source files before committing or uploading them."""
from pathlib import Path
import fnmatch
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
listed = subprocess.run(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=root, capture_output=True, check=True).stdout
files = sorted(set(name.decode('utf-8') for name in listed.split(b'\0') if name))
issues = []
old_brand = re.compile('cloud' + 'play' + r'(?:\s*x)?', re.I)
old_cli = re.compile(r'\b' + 'c' + 'p' + 'x' + r'\b', re.I)
secret_patterns = [
    re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    re.compile(r'\bAKIA[A-Z0-9]{16}\b'),
    re.compile(r'\bAIza[0-9A-Za-z_-]{35}\b'),
    re.compile(r'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(r'\bgithub_pat_[A-Za-z0-9_]{40,}\b'),
]
blocked_names = ['google-client.json', 'terabox-client.json', 'client_secret_*.json', 'rclone.conf', '*.key', '*.pfx', '*.p12', '.env', '.env.*', '*.db', '*.db-wal', '*.db-shm', '*.log', '*.trx']
blocked_parts = {'bin', 'obj', 'TestResults', '.work', 'dist', '.vs', '.claude'}
binary_suffixes = {'.ico', '.png'}
for name in files:
    relative = Path(name)
    path = root / relative
    if any(part in blocked_parts for part in relative.parts) or any(fnmatch.fnmatch(relative.name.lower(), pattern) for pattern in blocked_names):
        issues.append(f'{name}: private/generated file must not be tracked')
    if old_brand.search(name) or old_cli.search(name):
        issues.append(f'{name}: non-product name in path')
    if path.suffix.lower() in {'.exe', '.dll', '.msi', '.zip', '.tar', '.gz'}:
        issues.append(f'{name}: build/download artifact must not be tracked')
    if path.stat().st_size > 2 * 1024 * 1024:
        issues.append(f'{name}: unexpectedly large source file')
    if path.suffix.lower() in binary_suffixes:
        continue
    try:
        body = path.read_text(encoding='utf-8-sig')
    except UnicodeDecodeError:
        issues.append(f'{name}: unrecognized binary or text encoding')
        continue
    # The exact MIT copyright/grant for included source is required attribution.
    if name != 'licenses/Source-MIT-NOTICE.txt' and (old_brand.search(body) or old_cli.search(body)):
        issues.append(f'{name}: non-product branding')
    if re.search(r'\b0\.(?:8|9)\.\d+\b', body):
        issues.append(f'{name}: obsolete application release reference')
    for pattern in secret_patterns:
        if pattern.search(body):
            issues.append(f'{name}: possible credential/private key; inspect locally')
    if '\ufffd' in body:
        issues.append(f'{name}: invalid text encoding')
if issues:
    print('\n'.join(issues))
    sys.exit(1)
print(f'Source check passed: {len(files)} files; required source attribution retained separately.')
