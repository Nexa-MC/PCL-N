"""One-time Alpha 8 publication and archival; production Secrets are already migrated."""
import base64,json,os,re,subprocess,sys,time,urllib.error,urllib.parse,urllib.request
from pathlib import Path
OLD='Nexa-MC/PCL-N'
SOURCE='Nexa-MC/NexaCL'
UPDATES='Nexa-MC/NexaCL-Update'
TAG='v2.0.0.alpha.8'
ROOT=Path(os.environ['RUNNER_TEMP'])/'nexacl-transfer'
BRANCH=os.environ['TRANSFER_BRANCH']
NONCE=os.environ['TRANSFER_NONCE']
SOURCE_SHA=os.environ['TRANSFER_SOURCE_SHA']
WORKFLOW_FILES=['.github/workflows/nexacl-migrate-once.yml','eng/migration/nexacl_once.py']


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

def handoff(token, phase, refs=None, status=None):
    # The cloud connection has authorized Git/workflow write access. The PAT
    # remains limited to Secrets, Actions and repository administration.
    request_path = f'.migration/request-{NONCE}-{phase}.json'
    receipt_path = f'.migration/complete-{NONCE}-{phase}.json'
    request = {'binding': binding(), 'phase': phase, 'repository': SOURCE,
               'refs': refs or {}, 'status': status or ''}
    api(token, 'PUT', f'repos/{OLD}/contents/{request_path}', {
        'branch': BRANCH, 'message': f'ci: request authorized Git transfer ({phase})',
        'content': base64.b64encode(json.dumps(request, indent=2).encode()).decode()})
    log('Awaiting authorized cloud Git transfer: ' + phase)
    until = time.monotonic() + 900
    while time.monotonic() < until:
        item = api(token, 'GET', f'repos/{OLD}/contents/{receipt_path}?ref=' +
                   urllib.parse.quote(BRANCH, safe=''), missing=True)
        if item is not None:
            receipt = json.loads(base64.b64decode(item['content']))
            require(receipt.get('binding') == binding() and receipt.get('phase') == phase
                    and receipt.get('complete') is True, 'Git transfer receipt does not match this run.')
            if refs:
                remote = dict((line.split()[1], line.split()[0])
                              for line in git(token, 'ls-remote', f'https://github.com/{SOURCE}.git').splitlines())
                require(all(remote.get(ref) == sha for ref, sha in refs.items()),
                        'Independent destination Git ref verification failed.')
            log('Authorized cloud Git transfer independently verified: ' + phase)
            return
        time.sleep(5)
    raise RuntimeError('Authorized Git transfer deadline exceeded: ' + phase)

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

def retire_and_archive(token):
    status = (f'# NexaCL migration — verified publication\n\n'
              f'- Continuation: https://github.com/{SOURCE}/tree/main\n'
              f'- Exact verified source: `{SOURCE_SHA}`\n'
              f'- Signed Alpha 8: https://github.com/{SOURCE}/releases/tag/{TAG}\n'
              f'- Independently verified mirror: https://github.com/{UPDATES}/releases/tag/{TAG}\n\n'
              'Migrated Git history and release Secrets were preserved; main is the sole source branch. '
              'Historical release tags remain unchanged. Exact-commit destination CI and the tagged six-platform '
              'build succeeded. Source and update assets have identical names, lengths and SHA256 '
              'digests after independent publisher signature verification.\n\n'
              'This commit retires the one-time workflow and encrypted credential envelope. '
              'Old-source archival is the final operation immediately following this record; '
              'the repository archived flag is authoritative.\n')
    handoff(token, 'retire', status=status)
    for path in [*WORKFLOW_FILES, f'.migration/transfer-{NONCE}.json']:
        item = api(token, 'GET', f'repos/{OLD}/contents/{path}?ref=' +
                   urllib.parse.quote(BRANCH, safe=''), missing=True)
        require(item is None, 'Temporary migration file was not retired: ' + path)
    (ROOT / 'transport-private.pem').unlink(missing_ok=True)
    log('Migration workflow and encrypted credential envelope retired; transport private key removed.')
    log('Source CI, signed Alpha 8 and independently verified update mirror all passed; archiving old source.')
    api(token, 'PATCH', f'repos/{OLD}', {
        'description': 'Archived: development continues at Nexa-MC/NexaCL; updates at Nexa-MC/NexaCL-Update.',
        'homepage': 'https://github.com/Nexa-MC/NexaCL', 'archived': True})
    log('Old source archived after successful continuation and publication.')

def publish(token):
    source=api(token,'GET',f'repos/{SOURCE}')
    require(not source['archived'] and source['default_branch']=='main','Source must remain active on main.')
    heads=api(token,'GET',f'repos/{SOURCE}/branches?per_page=100')
    require(len(heads)==1 and heads[0]['name']=='main' and heads[0]['commit']['sha']==SOURCE_SHA,'Unexpected source branch topology or commit.')
    secrets=api(token,'GET',f'repos/{SOURCE}/actions/secrets?per_page=100')
    names={item['name'] for item in secrets['secrets']}
    require({'CURSEFORGE_API_KEY','PCLN_API_CLIENT_PFX_BASE64','GPG_PRIVATE_KEY'}<=names,'Required migrated release secret names are missing.')
    require(bool(names & {'CLIENT_ID','MS_CLIENT_ID','PCL_MS_CLIENT_ID'}),'Microsoft client ID secret is missing.')
    wait_run(token,SOURCE,'xsr-ci.yml',sha=SOURCE_SHA,event='push',minutes=65)
    current=api(token,'GET',f'repos/{SOURCE}/releases/tags/{TAG}',missing=True)
    if current is None:
        notes_file=api(token,'GET',f'repos/{SOURCE}/contents/docs/releases/2.0.0.alpha.8.md?ref={SOURCE_SHA}')
        notes=base64.b64decode(notes_file['content']).decode()
        tag=api(token,'GET',f'repos/{SOURCE}/git/ref/tags/{TAG}',missing=True)
        if tag is None:
            api(token,'PUT',f'repos/{SOURCE}/actions/workflows/launcher-build.yml/disable')
            try: handoff(token,'tag',{'refs/tags/'+TAG:SOURCE_SHA})
            finally: api(token,'PUT',f'repos/{SOURCE}/actions/workflows/launcher-build.yml/enable')
        else:
            require(tag['object']['type']=='commit' and tag['object']['sha']==SOURCE_SHA,'Existing Alpha 8 tag differs from the verified source.')
            api(token,'PUT',f'repos/{SOURCE}/actions/workflows/launcher-build.yml/enable')
        after=dispatch(token,SOURCE,'launcher-build.yml',TAG,{'release_notes':notes})
        wait_run(token,SOURCE,'launcher-build.yml',sha=SOURCE_SHA,event='workflow_dispatch',after=after,minutes=80)
        current=api(token,'GET',f'repos/{SOURCE}/releases/tags/{TAG}')
    require(not current['draft'] and current['prerelease'] and len(current['assets'])>=20,'Alpha 8 must be a complete public prerelease.')
    api(token,'PATCH',f'repos/{SOURCE}/releases/{current["id"]}',{'name':'NexaCL Firefly Alpha 8 · v2.0.0.alpha.8'})
    after=dispatch(token,UPDATES,'publish.yml','main',{'tag':TAG})
    wait_run(token,UPDATES,'publish.yml',event='workflow_dispatch',after=after,minutes=40)
    verify_release_pair(token)
    retire_and_archive(token)


def main():
    require(os.environ['GITHUB_REPOSITORY']==OLD,'Run only in the authorized old repository.')
    require(re.fullmatch(r'ci/migrate-nexacl-[0-9a-f]{12}',BRANCH) is not None,'Invalid transfer branch.')
    require(re.fullmatch(r'[0-9a-f]{40}',SOURCE_SHA) is not None,'Invalid verified source commit.')
    if sys.argv[1]=='prepare':prepare()
    elif sys.argv[1]=='publish':
        token=receive()
        try:publish(token)
        finally:(ROOT/'transport-private.pem').unlink(missing_ok=True)
    else:raise RuntimeError('Unknown phase.')

if __name__=='__main__':
    try:main()
    except Exception as error:
        log('Publication stopped safely: '+str(error));sys.exit(1)
