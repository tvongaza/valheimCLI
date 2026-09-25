"""Run the real development script with inert tools; never build or launch a game."""
import os
import pathlib
import subprocess
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / 'examples/dev-loop.sh'

class DeploymentGuardTests(unittest.TestCase):
    def check_status(self, output, status, expected):
        with tempfile.TemporaryDirectory() as d:
            root = pathlib.Path(d)
            binary = root / 'bin'
            binary.mkdir()
            game = root / 'game'
            (game / 'BepInEx/plugins').mkdir(parents=True)
            cli = binary / 'fake-cli'
            cli.write_text('#!/bin/sh\nprintf "%s\\n" "$FAKE_STATUS"\nexit "$FAKE_EXIT"\n')
            cli.chmod(0o755)
            dotnet = binary / 'dotnet'
            dotnet.write_text('#!/bin/sh\necho BUILD_REACHED\nexit 77\n')
            dotnet.chmod(0o755)
            env = {**os.environ, 'PATH': str(binary) + os.pathsep + os.environ['PATH'],
                   'VALHEIM_CLI': str(cli), 'VALHEIM_PATH': str(game),
                   'FAKE_STATUS': output, 'FAKE_EXIT': str(status)}
            run = subprocess.run(['bash', str(SCRIPT), 'unused.csproj'], env=env,
                                 capture_output=True, text=True)
            self.assertEqual(expected, run.returncode, run.stdout + run.stderr)
            self.assertEqual(expected == 77, 'BUILD_REACHED' in run.stdout)

    def test_running_without_plugin(self):
        self.check_status('game running=true local_process=true remote=false', 1, 5)
    def test_running_with_plugin(self):
        self.check_status('game local_process=true', 0, 5)
    def test_explicitly_stopped_with_unavailable_plugin(self):
        self.check_status('game local_process=false', 1, 77)
    def test_status_failure_without_process_evidence(self):
        self.check_status('', 1, 5)
    def test_conflicting_process_evidence_refuses(self):
        self.check_status('local_process=false\nlocal_process=true', 0, 5)

if __name__ == '__main__':
    unittest.main()
