"""Synthetic-only POSIX regression tests: never invokes pi, SSH, or production data."""
import importlib.util
import json
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('bridge', pathlib.Path(__file__).resolve().parents[2] / 'tools' / 'pi-bridge.py')
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


@unittest.skipUnless(os.name == 'posix', 'real POSIX process groups require POSIX')
class ProcessGroupsTests(unittest.TestCase):
    def run_task(self, mode):
        with tempfile.TemporaryDirectory(prefix='synthetic-pi-') as directory:
            marker = pathlib.Path(directory) / 'started.json'
            program = (
                'import os,signal,subprocess,sys,time,json; '
                'signal.signal(signal.SIGTERM, signal.SIG_IGN); '
                'child=subprocess.Popen([sys.executable,"-c","import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(30)"]); '
                'open(sys.argv[1],"w").write(json.dumps({"pid":os.getpid(),"pgid":os.getpgrp(),"child":child.pid})); '
                'time.sleep(30)'
            )
            if mode == 'leader_exit':
                program = program.rsplit('time.sleep(30)', 1)[0] + 'time.sleep(.2)'
            results = []
            runner = bridge.TaskRunner(results.append, [sys.executable, '-c', program, str(marker)], directory)
            thread = threading.Thread(target=runner.run, args=({'id': 'synthetic-task', 'prompt': 'synthetic', 'timeoutSec': 20 if mode in ('cancel', 'abort') else 1},))
            with patch.object(bridge, 'log'):
                thread.start()
                deadline = time.monotonic() + 3
                while not marker.exists() and time.monotonic() < deadline:
                    time.sleep(.01)
                self.assertTrue(marker.exists(), 'synthetic child did not start')
                while marker.stat().st_size == 0 and time.monotonic() < deadline:
                    time.sleep(.01)
                info = json.loads(marker.read_text())
                try:
                    self.assertEqual(info['pgid'], info['pid'], 'pi must own its group')
                    self.assertNotEqual(info['pgid'], os.getpgrp(), 'bridge group must stay isolated')
                    started = time.monotonic()
                    if mode == 'cancel':
                        runner.cancel()
                    elif mode == 'abort':
                        runner.abort()
                    thread.join(5)
                    self.assertFalse(thread.is_alive(), 'TERM-ignoring task must finish after bounded KILL')
                    self.assertLess(time.monotonic() - started, 5)
                    self.assertTrue(results and results[-1]['type'] == 'error')
                    # Linux zombies are already dead; check state, not merely kill(pid, 0).
                    status = pathlib.Path('/proc') / str(info['child']) / 'stat'
                    self.assertTrue(not status.exists() or status.read_text().split()[2] == 'Z', 'descendant survived')
                finally:
                    # Test cleanup never targets a group, even if the implementation is broken.
                    for pid in (info['child'], info['pid']):
                        try:
                            os.kill(pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    thread.join(3)

    def test_cancel_kills_only_owned_group_and_descendants(self):
        self.run_task('cancel')

    def test_abort_kills_only_owned_group_and_descendants(self):
        self.run_task('abort')

    def test_watchdog_escalates_term_ignoring_processes(self):
        self.run_task('timeout')

    def test_exited_leader_does_not_leave_pipe_holding_child(self):
        self.run_task('leader_exit')


class OwnershipSafetyTests(unittest.TestCase):
    class FakeProc:
        pid = 10001
        returncode = None

        def __init__(self):
            self.actions = []

        def poll(self):
            return self.returncode

        def terminate(self):
            self.actions.append('terminate')

        def kill(self):
            self.actions.append('kill')
            self.returncode = -9

        def wait(self, timeout=None):
            self.actions.append(('wait', timeout))
            if self.returncode is None:
                raise subprocess.TimeoutExpired('synthetic', timeout)
            return self.returncode

    def test_unknown_process_never_signals_a_group(self):
        proc = self.FakeProc()
        with patch.object(bridge.os, 'name', 'posix'), \
             patch.object(bridge.os, 'getpgid', create=True) as getpgid, \
             patch.object(bridge.os, 'killpg', create=True) as killpg:
            bridge._kill_tree(proc)
        getpgid.assert_not_called()
        killpg.assert_not_called()
        self.assertEqual(proc.actions, ['terminate', ('wait', 1.5), 'kill', ('wait', 1)])

    def test_already_exited_unknown_process_never_signals(self):
        proc = self.FakeProc()
        proc.returncode = 0
        with patch.object(bridge.os, 'name', 'posix'), \
             patch.object(bridge.os, 'killpg', create=True) as killpg:
            bridge._kill_tree(proc)
        killpg.assert_not_called()
        self.assertNotIn('kill', proc.actions)

    def test_disappeared_owned_group_is_not_signaled_again(self):
        proc = self.FakeProc()
        proc._pi_bridge_pgid = proc.pid
        with patch.object(bridge.os, 'name', 'posix'), \
             patch.object(bridge.os, 'getpgrp', return_value=10002, create=True), \
             patch.object(bridge.os, 'killpg', side_effect=[None, ProcessLookupError()], create=True) as killpg:
            bridge._kill_tree(proc)
        self.assertEqual([call.args for call in killpg.call_args_list], [(proc.pid, signal.SIGTERM), (proc.pid, 0)])
        self.assertIsNone(proc._pi_bridge_pgid)

    def test_spawn_failure_settles_error_without_cleanup_crash(self):
        results = []
        runner = bridge.TaskRunner(results.append, ['synthetic-pi'], '.')
        with patch.object(bridge.subprocess, 'Popen', side_effect=FileNotFoundError('synthetic')), patch.object(bridge, 'log'):
            runner.run({'id': 'synthetic-task', 'prompt': 'synthetic'})
        self.assertEqual(results[-1]['type'], 'error')
        self.assertIsNone(runner.proc)


if __name__ == '__main__':
    unittest.main(verbosity=2)
