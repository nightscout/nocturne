"""Merge only the Daily source already accepted by the HA repository."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
META = ROOT / '.personal/version.json'


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


def validate(meta):
    if not re.fullmatch(r'\d+\.\d+\.\d+', meta['version']):
        raise ValueError('Invalid extension version')
    if not re.fullmatch(r'[0-9a-f]{40}', meta['base_commit']):
        raise ValueError('Invalid upstream source commit')
    git('merge-base', '--is-ancestor', meta['base_commit'], 'HEAD')


def sync():
    current = json.loads(META.read_text())
    validate(current)
    url = 'https://api.github.com/repos/smokkelaar/nocturne-home-assistant/contents/upstream-latest.json?ref=main'
    headers = {'Accept': 'application/vnd.github.raw+json', 'User-Agent': 'nocturne-personal-sync'}
    if os.environ.get('GH_TOKEN'):
        headers['Authorization'] = 'Bearer ' + os.environ['GH_TOKEN']
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        approved = json.load(response)
    commit = approved['commit']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid approved Daily commit')
    if current['base_commit'] == commit:
        print('Personal already follows the approved Daily base.')
        return
    git('fetch', 'https://github.com/nightscout/nocturne.git', commit)
    git('merge-base', '--is-ancestor', current['base_commit'], commit)
    # A conflicting merge fails the job. Never reset/rebase away personal changes.
    git('merge', '--no-edit', commit)
    current.update(base_commit=commit, base_at=approved['commit_at'])
    META.write_text(json.dumps(current, indent=2) + '\n')
    validate(current)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if args.check:
        validate(json.loads(META.read_text()))
        print('Personal version and upstream ancestry verified; not a runtime test.')
    else:
        sync()
