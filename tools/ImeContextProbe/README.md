# ImeContextProbe

One-shot, read-only UIA3 diagnostics for a newly created disposable test window. Keep that window and its test editor focused while invoking this tool from a background command. Never use a user document or a browser containing private material.

```powershell
ImeContextProbe.exe --pid 12345 --test-window --expected-hwnd 0x123456 --label composing --output C:\absolute\existing-directory\ime-context.jsonl
```

`--pid` and `--test-window` are required. `--expected-hwnd` additionally binds to an exact foreground window. `--label` names a stage such as before/composing/cancelled; it does not trigger that stage. `--max-chars` bounds text/prefix reads (default 8192, maximum 32768). `--output` appends one JSON line to an explicitly supplied absolute file on a fixed local drive; its parent directory must already exist. Standard output always contains the same JSON result.

The probe verifies that the foreground and non-password focused UIA element belong to the explicit PID, then reads TextPattern/selection, TextPattern2/caret, TextEditPattern/active composition/conversion target. It outputs HWND, process/thread IDs, runtime/automation IDs and class names. It rechecks identity after capture and discards all captured source text if the boundary changed. It never activates windows, sends keys, terminates composition, edits text, accesses credentials or makes network requests.

An unavailable pattern or null active-composition range is a diagnostic result, not a global Windows limitation. A matched boundary is not proof of uninterrupted focus or composition-session identity. UTF-16 offsets are computed from UIA provider text prefixes and must not be used as editor write offsets. Reads are bounded; an out-of-bound prefix produces a null offset, and truncated source text is explicitly marked. UIA provider calls can still block; the parent harness should impose a process timeout.

Exit codes: `0` captured (some interfaces may be unavailable), `2` invalid CLI, `3` target rejected or boundary invalidated, `4` probe error, `5` local log write failure. Errors contain only exception type/HRESULT. Source text is emitted only after the required test-window acknowledgement and successful identity checks.

The project independently references FlaUI.UIA3 5.0.0 and Interop.UIAutomationClient 10.19041.0, matching existing diagnostics. It does not reference or build Input/App. It does not enumerate other windows or candidate hosts.
