"""Exercise the actual PowerShell source selector against disposable Git repositories."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SHELL = shutil.which('powershell') or shutil.which('pwsh')
REQUIRED = ['LICENSE.md', 'NOTICE', 'SOURCE.txt', 'README-CN.md', 'scripts/source-files.txt',
            'scripts/build-cn.ps1', 'scripts/source-package.ps1', 'Mahjong.Plugin.CN/Mahjong.Plugin.CN.csproj',
            'Mahjong.Cn.Core/Mahjong.Cn.Core.csproj', 'tools/MortalBridge/session.py', 'docs/cn/local-version-evidence.json']


@unittest.skipUnless(SHELL and shutil.which('git'), 'PowerShell and Git are required')
class SourcePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'source'
        self.root.mkdir()
        for name in REQUIRED:
            p = self.root / name
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text('synthetic source\n', encoding='utf-8')
        shutil.copyfile(ROOT / 'scripts/source-package.ps1', self.root / 'scripts/source-package.ps1')
        (self.root / 'scripts/source-files.txt').write_text('\n'.join(REQUIRED) + '\n', encoding='utf-8')
        self.git('init', '-q')
        self.git('config', 'user.name', 'Fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.commit()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), *args], stderr=subprocess.DEVNULL).decode().strip()

    def commit(self):
        self.git('add', '.')
        self.git('commit', '-qm', 'synthetic package fixture')

    def select(self):
        # Paths are supplied through the process environment, never interpolated as shell code.
        env = dict(os.environ, PACKAGE_TEST_ROOT=str(self.root))
        command = "$ErrorActionPreference='Stop'; . (Join-Path $env:PACKAGE_TEST_ROOT 'scripts/source-package.ps1'); try { $e=Get-ReviewedSourceEntries $env:PACKAGE_TEST_ROOT; @($e.Keys) | ConvertTo-Json -Compress } catch { exit 7 }"
        result = subprocess.run([SHELL, '-NoProfile', '-NonInteractive', '-Command', command], env=env, capture_output=True)
        return result.returncode, result.stdout

    def test_untracked_secrets_and_notes_never_enter_selected_source(self):
        for name in ('.env.production', 'private.txt', 'private.json', 'extra.cs'):
            (self.root / name).write_text('synthetic only', encoding='utf-8')
        code, output = self.select()
        self.assertEqual(0, code)
        self.assertEqual(set(REQUIRED), set(json.loads(output)))

    def test_dirty_tracked_source_rejects_formal_package(self):
        (self.root / 'NOTICE').write_text('changed', encoding='utf-8')
        self.assertEqual(7, self.select()[0])

    def test_new_tracked_file_requires_explicit_review(self):
        (self.root / 'extra.cs').write_text('synthetic', encoding='utf-8')
        self.commit()
        self.assertEqual(7, self.select()[0])

    def test_private_file_cannot_be_added_even_to_reviewed_list(self):
        (self.root / '.env.production').write_text('synthetic only', encoding='utf-8')
        with (self.root / 'scripts/source-files.txt').open('a', encoding='utf-8') as f:
            f.write('.env.production\n')
        self.commit()
        self.assertEqual(7, self.select()[0])

    def test_archive_hash_verification_detects_changed_or_missing_source(self):
        commit = self.git('rev-parse', 'HEAD')
        manifest = {'baseCommit': commit, 'files': [dict(path=n, sha256=hashlib.sha256((self.root / n).read_bytes()).hexdigest()) for n in REQUIRED]}
        (self.root / 'source-file-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        # Leave Git metadata outside this extracted-source fixture, within the same temp directory.
        (self.root / '.git').rename(Path(self.temp.name) / 'git-metadata')
        self.assertEqual(0, self.select()[0])
        (self.root / 'NOTICE').write_text('tampered', encoding='utf-8')
        self.assertEqual(7, self.select()[0])
        (self.root / 'NOTICE').unlink()
        self.assertEqual(7, self.select()[0])

    def test_public_logs_remove_workspace_profile_and_machine_identifiers(self):
        env = dict(os.environ, PACKAGE_TEST_ROOT=str(self.root), USERPROFILE=r'C:\Users\FixtureUser',
                   USERNAME='FixtureUser', COMPUTERNAME='FixtureMachine')
        command = r". (Join-Path $env:PACKAGE_TEST_ROOT 'scripts/source-package.ps1'); ConvertTo-PublicBuildText ($env:PACKAGE_TEST_ROOT + ' C:\Users\FixtureUser\file FixtureUser@FixtureMachine FixtureUser_FixtureMachine_date') $env:PACKAGE_TEST_ROOT"
        result = subprocess.run([SHELL, '-NoProfile', '-NonInteractive', '-Command', command], env=env, capture_output=True)
        self.assertEqual(0, result.returncode)
        self.assertNotIn(b'FixtureUser', result.stdout)
        self.assertNotIn(b'FixtureMachine', result.stdout)
        self.assertNotIn(str(self.root).encode(), result.stdout)


if __name__ == '__main__':
    unittest.main()
