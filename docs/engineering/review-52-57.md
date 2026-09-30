# Review remediation: #52 and #57

## Scope and observed contracts

- `tools/pi-bridge.py`: POSIX pi was launched in the bridge's process group; `_kill_tree` discovered that shared group and sent TERM without a bounded escalation.
- `ReverseWsTransport`: async header reads used `ReadTimeout` (synchronous-only), and the accept loop awaited the entire connection. A silent/partial header could therefore block valid subsequent clients indefinitely.
- Preserve OneBot authorization, the 16 KiB header cap, and one active protocol connection. No settings, app wiring, fetch policy, or frontend changes.

## Implementation plan

1. Launch POSIX pi in its own session, record owned process-group identity at spawn, and terminate only that group. Bound TERM grace and KILL reap; unknown processes use single-process fallback. Keep Windows taskkill behavior.
2. Give each reverse handshake a 10-second absolute cancellation budget, independent from transport lifetime. Accept and handle pending handshakes independently with a finite pending limit; publish only authenticated upgrades and close owned sockets on timeout/failure/stop.
3. Add isolated Python process-group tests and a source-linked .NET reverse transport probe using synthetic loopback requests. Confirm the tests fail before the fixes, then pass afterwards. Avoid the temporarily broken main build and all other agents' files.

## Verification

```powershell
python -m unittest discover -s tests/BotAgent.BridgeProbe -p test_transport.py -v
python -m unittest discover -s tests/BotAgent.BridgeProbe -p test_process_groups.py -v
wsl -d Ubuntu -- python3 tests/BotAgent.BridgeProbe/test_process_groups.py
dotnet run --project tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj -c Release
git diff --check -- tools/pi-bridge.py src/BotAgent.Headless/Services/OneBot/ReverseWsTransport.cs tests/BotAgent.BridgeProbe docs/engineering/review-52-57.md tests/BotAgent.ReverseTransportProbe
```

The WSL command must run from the repository's mounted working directory and uses only synthetic subprocesses. Tests must not run the real pi CLI, SSH, production sockets, stored messages, or member data.

## Implemented behavior and evidence

- POSIX spawn records a private group established by `start_new_session=True`. Unknown process objects never trigger `getpgid`/`killpg`. Cleanup consumes the ownership record under a per-process lock and returns immediately when the group disappears. TERM grace is 1.5 seconds; KILL reap is bounded to one second. The watchdog captures its own task process, including the case where the leader exits but a descendant still holds stdout/stderr.
- Reverse handshakes use one 10-second linked async cancellation budget (including response writes), with at most 16 pending handshakes. A valid authenticated upgrade may replace the single active client; stale handlers do not clear the replacement or deliver stale text. Stop closes pending and active sockets.
- Before fixes: all three initial real POSIX isolation tests failed on group identity; the source-linked reverse probe reported six failures.
- After fixes: existing Python transport probe passed 5/5; POSIX regression suite passed 8/8 on local Ubuntu (four portable ownership/spawn safety checks plus four real task/descendant checks). Windows runs the four portable checks and explicitly skips the four POSIX-only cases. Python `py_compile` passed.
- The .NET probe is compiled directly against the two transport source files, not an old binary or the application project; its checks cover later valid clients, silent/trickling handshake deadlines, authorization failure, header cap, active replacement, finite pending capacity, and stop cleanup.
- Final isolated .NET build: 0 warnings / 0 errors. All 13 probe assertions passed on three consecutive runs (`failures=0` each). Replacement routing is verified by sending an action to the new client, not merely by observing an HTTP 101 response. Target-source `git diff --check` passed.
- The application-wide build/integration suite is intentionally left to the parent agent handling concurrent changes; this result does not claim that unrelated worktree edits are build-clean.

## Boundaries and residual risks

- POSIX process groups cannot contain descendants that deliberately escape via a new session; no claim of OS-level sandboxing is made.
- The reverse transport remains a minimal RFC6455 implementation; frame validation and size hardening are outside this issue scope.
- Finite pending handshakes protect the process, but slot saturation can still cause legitimate retries; deployments should retain network access restrictions and configured OneBot authorization.
