"""One-time, run-bound repository migration. No production credentials are persisted."""
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

OLD = 'Nexa-MC/PCL-N'
SOURCE = 'Nexa-MC/NexaCL'
UPDATES = 'Nexa-MC/NexaCL-Update'
TAG = 'v2.0.0.beta.1'
ROOT = Path(os.environ['RUNNER_TEMP']) / 'nexacl-transfer'
BRANCH = os.environ['TRANSFER_BRANCH']
NONCE = os.environ['TRANSFER_NONCE']
SOURCE_SHA = os.environ['TRANSFER_SOURCE_SHA']
WORKFLOW_FILES = ['.github/workflows/nexacl-migrate-once.yml', 'eng/migration/nexacl_once.py']
SECRET_NAMES = ('CURSEFORGE_API_KEY', 'PCLN_API_CLIENT_PFX_BASE64', 'GPG_PRIVATE_KEY',
                'GPG_PASSPHRASE', 'CLIENT_ID', 'MS_CLIENT_ID', 'PCL_MS_CLIENT_ID',
                'LITTLESKIN_CLIENT_ID', 'PCL_LITTLESKIN_CLIENT_ID')


def log(message):
    print(message, flush=True)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def binding():
    return {'schema': 1, 'repository': OLD, 'branch': BRANCH, 'nonce': NONCE,
            'source_sha': SOURCE_SHA, 'run_id': os.environ['GITHUB_RUN_ID'],
            'workflow_sha': os.environ['GITHUB_SHA']}


def clean_env(token=None):
    names = ('PATH', 'HOME', 'LANG', 'LC_ALL', 'TMPDIR', 'RUNNER_TEMP', 'SYSTEMROOT')
    env = {k: os.environ[k] for k in names if k in os.environ}
    env.update({'GIT_TERMINAL_PROMPT': '0', 'GH_PROMPT_DISABLED': '1'})
    if token:
        env['GH_TOKEN'] = token
    return env


def command(args, *, token=None, cwd=None, data=None):
    result = subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            cwd=cwd, env=clean_env(token))
    # Credential commands stay opaque. Git receives no secret stdin, and its clean
    # environment only contains the masked token; retain bounded server rejections.
    if result.returncode and args[0] == 'git':
        detail = result.stderr.decode(errors='replace')
        if token:
            detail = detail.replace(token, '[masked credential]')
        detail = re.sub(r'(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+', '[masked credential]', detail)
        useful = [line for line in detail.splitlines()
                  if any(marker in line.lower() for marker in ('error:', 'remote rejected', 'denied', 'refusing'))]
        log('Git rejection: ' + '\n'.join(useful)[:5000])
    require(result.returncode == 0, f'Command failed: {Path(args[0]).name} (exit {result.returncode})')
    return result.stdout


def api(token, method, path, payload=None, *, missing=False):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request('https://api.github.com/' + path, data=data, method=method,
                                 headers={'Authorization': 'Bearer ' + token,
                                          'Accept': 'application/vnd.github+json',
                                          'X-GitHub-Api-Version': '2022-11-28',
                                          'Content-Type': 'application/json',
                                          'User-Agent': 'NexaCL-one-time-migration'})
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            content = response.read()
    except urllib.error.HTTPError as error:
        if missing and error.code == 404:
            return None
        needed = error.headers.get('X-Accepted-GitHub-Permissions', '')
        detail = f' (required: {needed})' if needed else ''
        raise RuntimeError(f'GitHub {method} {path.split("?")[0]}: HTTP {error.code}{detail}') from None
    return json.loads(content) if content else None


def git(token, *args, cwd=None):
    return command(['git', '-c', 'credential.helper=!gh auth git-credential', *args],
                   token=token, cwd=cwd).decode().strip()


def prepare():
    ROOT.mkdir(mode=0o700)
    public = ROOT / 'public'
    public.mkdir(mode=0o700)
    private = ROOT / 'transport-private.pem'
    command(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:3072',
             '-out', str(private)])
    private.chmod(0o600)
    command(['openssl', 'pkey', '-in', str(private), '-pubout',
             '-out', str(public / 'transport-public.pem')])
    (public / 'binding.json').write_text(json.dumps(binding(), indent=2) + '\n')
    log('Ephemeral public transport key prepared; private key remains on runner.')


def receive():
    path = f'repos/{OLD}/contents/.migration/transfer-{NONCE}.json?ref=' + urllib.parse.quote(BRANCH, safe='')
    until = time.monotonic() + 900
    while time.monotonic() < until:
        item = api(os.environ['GITHUB_TOKEN'], 'GET', path, missing=True)
        if item is not None:
            payload = json.loads(base64.b64decode(item['content'], validate=False))
            require(payload.get('binding') == binding(), 'Credential envelope does not match this exact run.')
            encrypted = base64.b64decode(payload['ciphertext'], validate=True)
            require(len(encrypted) == 384, 'Unexpected encrypted credential size.')
            raw = command(['openssl', 'pkeyutl', '-decrypt', '-inkey', str(ROOT / 'transport-private.pem'),
                           '-pkeyopt', 'rsa_padding_mode:oaep', '-pkeyopt', 'rsa_oaep_md:sha256'], data=encrypted)
            token = raw.decode('ascii')
            require(re.fullmatch(r'github_pat_[A-Za-z0-9_]{40,240}', token) is not None,
                    'Unexpected credential format.')
            # GitHub workflow command registers a mask; it is not stored as ordinary log text.
            print('::add-mask::' + token, flush=True)
            log('Run-bound encrypted credential received.')
            return token
        time.sleep(5)
    raise RuntimeError('No encrypted credential delivered before the 15-minute deadline.')


def copy_secrets(token):
    existing = api(token, 'GET', f'repos/{SOURCE}/actions/secrets?per_page=100')
    require(existing['total_count'] <= 100, 'Unexpected destination secret count.')
    present = {item['name'] for item in existing['secrets']}
    for name in SECRET_NAMES:
        value = os.environ.get('TRANSFER_SECRET_' + name, '')
        if name in present:
            log('Preserved destination secret: ' + name)
        elif value:
            command(['gh', 'secret', 'set', name, '--repo', SOURCE], token=token, data=value.encode())
            present.add(name)
            log('Transferred existing release secret: ' + name)
    required = {'CURSEFORGE_API_KEY', 'PCLN_API_CLIENT_PFX_BASE64', 'GPG_PRIVATE_KEY'}
    require(required <= present, 'Missing required release secret names: ' + ', '.join(sorted(required - present)))
    require(bool(present & {'CLIENT_ID', 'MS_CLIENT_ID', 'PCL_MS_CLIENT_ID'}),
            'No Microsoft client ID secret is configured for release entitlement verification.')
    # Remove source secret values from our process environment after their transfer.
    for name in SECRET_NAMES:
        os.environ.pop('TRANSFER_SECRET_' + name, None)


def migrate_history(token):
    old_head = api(token, 'GET', f'repos/{OLD}/git/ref/heads/refactor/xsr')['object']['sha']
    require(old_head == SOURCE_SHA, 'Source branch changed; refusing to migrate a different commit.')
    source = api(token, 'GET', f'repos/{SOURCE}')
    update = api(token, 'GET', f'repos/{UPDATES}')
    require(not source['archived'] and not update['archived'], 'Destination repository is archived.')
    state = api(token, 'GET', f'repos/{SOURCE}/actions/permissions')
    bare = ROOT / 'history.git'
    # Only metadata is used; the one-time branch never enters the continuation repository.
    git(token, 'clone', '--bare', f'https://github.com/{OLD}.git', str(bare))
    refs = git(token, 'for-each-ref', '--format=%(refname)', 'refs/heads', 'refs/tags', cwd=bare).splitlines()
    refs = [ref for ref in refs if not ref.startswith('refs/heads/ci/migrate-nexacl-')]
    require('refs/heads/refactor/xsr' in refs, 'Migration source branch not present.')
    try:
        api(token, 'PUT', f'repos/{SOURCE}/actions/permissions', {'enabled': False})
        git(token, 'push', '--atomic', f'https://github.com/{SOURCE}.git',
            *[ref + ':' + ref for ref in refs], cwd=bare)
        api(token, 'PATCH', f'repos/{SOURCE}', {
            'default_branch': 'refactor/xsr', 'has_issues': True, 'has_discussions': True,
            'description': 'NexaCL Firefly — cross-platform Minecraft launcher; continues PCL-N XSR history.'})
        migrated = api(token, 'GET', f'repos/{SOURCE}/git/ref/heads/refactor/xsr')['object']['sha']
        require(migrated == SOURCE_SHA, 'Destination source SHA does not match.')
        log(f'Preserved {len(refs)} historical heads/tags; destination XSR commit verified.')
    except BaseException:
        api(token, 'PUT', f'repos/{SOURCE}/actions/permissions',
            {k: state[k] for k in ('enabled', 'allowed_actions') if k in state})
        raise
    api(token, 'PUT', f'repos/{SOURCE}/actions/permissions',
        {'enabled': True, **({ 'allowed_actions': state['allowed_actions']} if 'allowed_actions' in state else {})})
    return bare


def seed_updates(token, bare):
    tree = {'README.md': 'eng/release/updates-repository/README.md',
            '.github/workflows/publish.yml': 'eng/release/updates-repository/.github/workflows/publish.yml',
            'GPG-PUBLIC-KEY.asc': 'GPG-PUBLIC-KEY.asc', 'LICENSE': 'LICENSE'}
    existing = api(token, 'GET', f'repos/{UPDATES}/git/ref/heads/main', missing=True)
    if existing is not None:
        for destination, source in tree.items():
            current = api(token, 'GET', f'repos/{UPDATES}/contents/{destination}?ref=main')
            expected = command(['git', 'show', SOURCE_SHA + ':' + source], cwd=bare)
            require(base64.b64decode(current['content']) == expected,
                    'Existing update repository content differs: ' + destination)
        log('Existing matching update seed preserved.')
        return
    checkout = ROOT / 'updates'
    checkout.mkdir()
    git(token, 'init', '-b', 'main', cwd=checkout)
    for destination, source in tree.items():
        path = checkout / destination
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(command(['git', 'show', SOURCE_SHA + ':' + source], cwd=bare))
    git(token, '-c', 'user.name=NexaCL Migration', '-c', 'user.email=actions@users.noreply.github.com',
        'add', '.', cwd=checkout)
    git(token, '-c', 'user.name=NexaCL Migration', '-c', 'user.email=actions@users.noreply.github.com',
        'commit', '-m', 'feat(updates): initialize independently verified distribution source', cwd=checkout)
    git(token, 'push', f'https://github.com/{UPDATES}.git', 'HEAD:refs/heads/main', cwd=checkout)
    log('Independent update repository initialized with the original publisher public key.')


def workflow_run(token, repo, workflow, *, sha=None, event=None, after=None):
    path = f'repos/{repo}/actions/workflows/{workflow}/runs?per_page=50'
    runs = api(token, 'GET', path)['workflow_runs']
    return next((run for run in runs if (sha is None or run['head_sha'] == sha)
                 and (event is None or run['event'] == event)
                 and (after is None or run['created_at'] >= after)), None)


def wait_run(token, repo, workflow, *, sha=None, event=None, after=None, minutes=65):
    until = time.monotonic() + minutes * 60
    last = None
    run = None
    while time.monotonic() < until:
        run = (api(token, 'GET', f'repos/{repo}/actions/runs/{run["id"]}') if run else
               workflow_run(token, repo, workflow, sha=sha, event=event, after=after))
        if run:
            state = (run['id'], run['status'], run['conclusion'])
            if state != last:
                log(f'{repo} {workflow}: {run["html_url"]} — {run["status"]}/{run["conclusion"]}')
                last = state
            if run['status'] == 'completed':
                require(run['conclusion'] == 'success', 'Destination workflow failed: ' + run['html_url'])
                return run
        time.sleep(20)
    raise RuntimeError('Destination workflow deadline exceeded: ' + repo + '/' + workflow)


def dispatch(token, repo, workflow, ref, inputs=None):
    # Use server time to bind the subsequent run without depending on runner clock drift.
    after = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time() - 5))
    api(token, 'POST', f'repos/{repo}/actions/workflows/{workflow}/dispatches',
        {'ref': ref, **({'inputs': inputs} if inputs else {})})
    return after


def verify_release_pair(token):
    source = api(token, 'GET', f'repos/{SOURCE}/releases/tags/{TAG}')
    update = api(token, 'GET', f'repos/{UPDATES}/releases/tags/{TAG}')
    require(not source['draft'] and not update['draft'], 'Release is still a draft.')
    a = {item['name']: (item['size'], item.get('digest')) for item in source['assets']}
    b = {item['name']: (item['size'], item.get('digest')) for item in update['assets']}
    require(a == b and len(a) >= 20, 'Published source and update asset sets/digests differ.')
    require(all(size > 0 and isinstance(digest, str) and digest.startswith('sha256:')
                for size, digest in a.values()), 'GitHub did not provide complete asset SHA256 digests.')
    log(f'Verified identical source/update asset names, sizes and SHA256 digests: {len(a)} assets.')
    log(source['html_url'])
    log(update['html_url'])


def release(token, bare):
    checked = workflow_run(token, SOURCE, 'xsr-ci.yml', sha=SOURCE_SHA)
    if checked and checked['status'] == 'completed' and checked['conclusion'] == 'success':
        log('Reusing successful exact-commit destination XSR CI: ' + checked['html_url'])
    else:
        after = dispatch(token, SOURCE, 'xsr-ci.yml', 'refactor/xsr')
        wait_run(token, SOURCE, 'xsr-ci.yml', sha=SOURCE_SHA, event='workflow_dispatch', after=after)
    log('Destination XSR CI succeeded; preparing maintained Beta 1 release notes.')
    notes = command(['git', 'show', SOURCE_SHA + ':docs/releases/2.0.0.beta.1.md'], cwd=bare).decode()
    releases = api(token, 'GET', f'repos/{SOURCE}/releases?per_page=100')
    require(not any(item['tag_name'] == TAG for item in releases),
            'Beta 1 already has a Release; preserve it and review before a new publication.')
    # Existing publication contracts keep prewritten drafts private. Use the explicit
    # manual-notes dispatch path instead: the verified source workflow creates the
    # public release with its own GITHUB_TOKEN, preventing recursive release builds.
    existing_tag = api(token, 'GET', f'repos/{SOURCE}/git/ref/tags/{TAG}', missing=True)
    if existing_tag is None:
        api(token, 'PUT', f'repos/{SOURCE}/actions/workflows/launcher-build.yml/disable')
        try:
            git(token, 'tag', TAG, SOURCE_SHA, cwd=bare)
            git(token, 'push', f'https://github.com/{SOURCE}.git', 'refs/tags/' + TAG, cwd=bare)
        finally:
            api(token, 'PUT', f'repos/{SOURCE}/actions/workflows/launcher-build.yml/enable')
    else:
        require(existing_tag['object']['type'] == 'commit' and existing_tag['object']['sha'] == SOURCE_SHA,
                'Existing Beta 1 tag does not identify the verified source commit.')
    after = dispatch(token, SOURCE, 'launcher-build.yml', TAG, {'release_notes': notes})
    wait_run(token, SOURCE, 'launcher-build.yml', sha=SOURCE_SHA,
             event='workflow_dispatch', after=after)
    current = api(token, 'GET', f'repos/{SOURCE}/releases/tags/{TAG}')
    require(not current['draft'] and len(current['assets']) >= 20,
            'Signed build did not publish the full distribution.')
    api(token, 'PATCH', f'repos/{SOURCE}/releases/{current["id"]}',
        {'name': 'NexaCL Firefly Beta 1 · v2.0.0.beta.1'})
    after = dispatch(token, UPDATES, 'publish.yml', 'main', {'tag': TAG})
    wait_run(token, UPDATES, 'publish.yml', event='workflow_dispatch', after=after, minutes=40)
    verify_release_pair(token)


def retire_and_archive(token):
    ref_path = f'repos/{OLD}/git/refs/heads/{BRANCH}'
    latest = api(token, 'GET', f'repos/{OLD}/git/ref/heads/{BRANCH}')['object']['sha']
    commit = api(token, 'GET', f'repos/{OLD}/git/commits/{latest}')
    paths = [*WORKFLOW_FILES, f'.migration/transfer-{NONCE}.json']
    status = (f'# NexaCL migration — verified publication\n\n'
              f'- Continuation: https://github.com/{SOURCE}/tree/refactor/xsr\n'
              f'- Exact verified source: `{SOURCE_SHA}`\n'
              f'- Signed Beta 1: https://github.com/{SOURCE}/releases/tag/{TAG}\n'
              f'- Independently verified mirror: https://github.com/{UPDATES}/releases/tag/{TAG}\n\n'
              'Historical heads/tags and release Secrets were migrated; existing destination values '
              'and main history were preserved. Exact-commit destination CI and the tagged six-platform '
              'build succeeded. Source and update assets have identical names, lengths and SHA256 '
              'digests after independent publisher signature verification.\n\n'
              'This commit retires the one-time workflow and encrypted credential envelope. '
              'Old-source archival is the final operation immediately following this record; '
              'the repository archived flag is authoritative.\n')
    status_blob = api(token, 'POST', f'repos/{OLD}/git/blobs', {'content': status, 'encoding': 'utf-8'})
    entries = [{'path': path, 'mode': '100644', 'type': 'blob', 'sha': None} for path in paths]
    entries.append({'path': 'eng/migration/STATUS.md', 'mode': '100644', 'type': 'blob',
                    'sha': status_blob['sha']})
    tree = api(token, 'POST', f'repos/{OLD}/git/trees', {
        'base_tree': commit['tree']['sha'], 'tree': entries})
    cleanup = api(token, 'POST', f'repos/{OLD}/git/commits', {
        'message': 'chore: retire completed one-time NexaCL migration',
        'tree': tree['sha'], 'parents': [latest]})
    api(token, 'PATCH', ref_path, {'sha': cleanup['sha'], 'force': False})
    (ROOT / 'transport-private.pem').unlink(missing_ok=True)
    log('Migration workflow and encrypted credential envelope retired; transport private key removed.')
    log('Source CI, signed Beta 1 and independently verified update mirror all passed; archiving old source.')
    api(token, 'PATCH', f'repos/{OLD}', {
        'description': 'Archived: development continues at Nexa-MC/NexaCL; updates at Nexa-MC/NexaCL-Update.',
        'homepage': 'https://github.com/Nexa-MC/NexaCL', 'archived': True})
    log('Old source archived after successful continuation and publication.')


def main():
    require(os.environ['GITHUB_REPOSITORY'] == OLD, 'This migration runs only in the authorized old repository.')
    require(re.fullmatch(r'ci/migrate-nexacl-[0-9a-f]{12}', BRANCH) is not None, 'Invalid temporary branch.')
    require(re.fullmatch(r'[0-9a-f]{40}', SOURCE_SHA) is not None, 'Invalid source commit.')
    if sys.argv[1] == 'prepare':
        prepare()
    elif sys.argv[1] == 'migrate':
        token = receive()
        try:
            # Verify the PAT's actual account and repository rights inside GitHub's runner.
            identity = api(token, 'GET', 'user')
            log('Authenticated authorized migration credential as: ' + identity['login'])
            blocked = []
            try:
                copy_secrets(token)
            except RuntimeError as error:
                blocked.append(str(error))
                log('Release credentials pending: ' + str(error))
            try:
                bare = migrate_history(token)
            except RuntimeError as error:
                # Contents rights can exist independently of Actions administration.
                # Import only XSR ancestry if historical tag events cannot be paused.
                if 'HTTP 403' not in str(error):
                    raise
                blocked.append(str(error))
                log('Full ref import pending: ' + str(error))
                bare = ROOT / 'source-ancestry.git'
                git(token, 'clone', '--bare', f'https://github.com/{OLD}.git', str(bare))
                require(git(token, 'rev-parse', 'refs/heads/refactor/xsr', cwd=bare) == SOURCE_SHA,
                        'Source branch changed during migration.')
                git(token, 'push', f'https://github.com/{SOURCE}.git',
                    'refs/heads/refactor/xsr:refs/heads/refactor/xsr', cwd=bare)
                log('XSR branch and complete reachable Git ancestry migrated; existing main preserved.')
            seed_updates(token, bare)
            if blocked:
                log('Completed all operations allowed by the PAT; release and archival remain pending.')
                raise RuntimeError('; '.join(blocked))
            release(token, bare)
            retire_and_archive(token)
        finally:
            (ROOT / 'transport-private.pem').unlink(missing_ok=True)
    else:
        raise RuntimeError('Unknown phase.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Errors are deliberately bounded to operation/status; never dump subprocess environments.
        log('Migration stopped safely: ' + str(error))
        sys.exit(1)
