#!/usr/bin/env python3
"""Build a LOCAL preview directory. Does not upload, deploy, launch or stop a game."""
import argparse, hashlib, json, pathlib, re, shutil, subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)
def output(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--version', default='0.1.0-preview.4')
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+-preview\.\d+', args.version):
        parser.error('Use an explicit preview version.')
    if output('git', 'status', '--porcelain'):
        parser.error('Commit source changes before packaging so the candidate has an auditable revision.')
    inputs = json.loads((ROOT / 'docs/integration-inputs.json').read_text())
    for item in inputs['upstreamPRs']:
        run('git', 'merge-base', '--is-ancestor', item['headCommit'], 'HEAD')
    for name in ('Valheim.Cli.Testing',):
        project = (ROOT / f'Toolkit/{name}/{name}.csproj').read_text()
        if f'<Version>{args.version}</Version>' not in project:
            parser.error('Package version must match all project dependency versions; update and commit them first.')
    dest = args.output.resolve()
    if dest.exists(): parser.error('Output must be a new directory; prior candidates are never overwritten.')
    dest.mkdir(parents=True)
    commit = output('git', 'rev-parse', 'HEAD')
    run('dotnet', 'build', 'valheimCLI.csproj', '-c', 'Release', '--no-restore', '-m:1')
    (dest / 'plugin').mkdir()
    shutil.copy2(ROOT / 'bin/Release/valheimCLI.dll', dest / 'plugin/valheimCLI.dll')
    run('dotnet', 'publish', 'CLI/valheim-cli.csproj', '-c', 'Release', '--no-restore', '-m:1', '-p:UseAppHost=false', '-o', str(dest / 'cli'))
    for name in ('Valheim.Cli.Testing',):
        run('dotnet', 'pack', f'Toolkit/{name}/{name}.csproj', '-c', 'Release', '--no-restore', '-m:1', f'-p:PackageVersion={args.version}', '-o', str(dest / 'packages'))
    shutil.copy2(ROOT / 'LICENSE', dest / 'LICENSE')
    shutil.copy2(ROOT / 'THIRD-PARTY-NOTICES.txt', dest / 'THIRD-PARTY-NOTICES.txt')
    shutil.copy2(ROOT / 'docs/integration-release.md', dest / 'RELEASE-GATES.md')
    shutil.copy2(ROOT / 'docs/testing-toolkit.md', dest / 'TESTING.md')
    run('git', 'archive', '--format=tar', '-o', str(dest / 'source.tar'), commit)
    # Allowlist the plugin payload: game, Unity, BepInEx and Roads binaries are never bundled.
    forbidden = ('assembly_valheim', 'UnityEngine', 'BepInEx', 'ProceduralRoads')
    for path in dest.rglob('*.dll'):
        if path.name.startswith(forbidden): raise RuntimeError('Unexpected redistributable: ' + path.name)
    files = {str(p.relative_to(dest)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(dest.rglob('*')) if p.is_file()}
    manifest = {'version': args.version, 'sourceCommit': commit, 'status': 'local-candidate-not-published',
                'upstreamOwner': 'jneb802/valheimCLI', 'upstreamPRs': inputs['upstreamPRs'],
                'publication': 'Not published. Preview includes unmerged owner PRs and testing-framework work.', 'sha256': files}
    (dest / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    files['manifest.json'] = hashlib.sha256((dest / 'manifest.json').read_bytes()).hexdigest()
    (dest / 'SHA256SUMS').write_text(''.join(f'{value}  {name}\n' for name, value in sorted(files.items())))
    print(dest)
if __name__ == '__main__': main()
