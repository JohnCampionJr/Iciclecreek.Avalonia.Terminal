# Iciclecreek.Terminal

## 0.1.2
### Patch Changes

- `ProcessExited` is now raised even when the child process is slow to be reaped. Before, a process
  that ended but could not be reaped within a second raised no exit event at all, so a host waiting
  on one never learned the process had finished — a terminal could sit showing a dead shell as still
  running, indefinitely.

  The exit code is still never guessed. When it cannot be read, the new
  `ProcessExitedEventArgs.ExitCodeKnown` is `false` instead of reporting `0`, which would look like
  success. Existing code reading `ExitCode` is unaffected.

## 0.1.1
### Patch Changes

- Fix `ProcessExited` reporting exit code 0 for a process that exited non-zero.
  
  Two paths race to report an exit behind one interlock: the process-exited event, which carries the
  real code, and the read loop's EOF fallback, which read `ExitCode` immediately. EOF means the child
  closed its end, which can beat the child being *reaped* — and until it is, `ExitCode` is still 0. So
  whenever the EOF path won, a failed process was reported as a clean exit. Measured: 20 runs of
  `sh -c "exit 3"` reported 0 once, and concurrent runs far more often.
  
  It matters more than the frequency suggests. The reported code is usually all a host has to go on —
  the buffer's own "Process exited with code" line is routinely cleared on the way back to idle — and
  0 is the one wrong answer that reads as *success*.
  
  The EOF path now reaps the child before reading its status, and does so before claiming the
  interlock, so the process-exited event can still win and report authoritatively. A child that will
  not reap within 1s leaves the interlock unclaimed rather than inventing a code.
  
  Also fixed alongside it: the read loop consulted a mutable field across awaits, so a relaunch
  mid-read could leave a stale loop acting on the *next* process — waiting on it, reading its exit
  code, and claiming the interlock meant for it. Each loop now holds the connection it reads.
  

## 0.1.0
### Minor Changes

- Fork of tomlm/Iciclecreek.Avalonia.Terminal, branched at e602ff0 (upstream 2.0.3), renamed so the assembly, namespace and package are all Iciclecreek.Terminal and the theme resource is avares://Iciclecreek.Terminal/Themes/Generic.axaml. Targets net10.0 against Avalonia 12.1.0; published to the private feed, not nuget.org.
  
  Scrolling is the bulk of the divergence. ICustomHitTest makes the whole view an input surface — Avalonia hit-tests what a control DREW, not the rect it occupies, so the view was reachable by the pointer only over pixels carrying text and wheel events over blank space fell through to whatever sat behind it (offered upstream as #29). Fractional wheel deltas now accumulate instead of each truncating to zero, which is why a trackpad's stream of ~0.05 events could not scroll at all; a direction change drops the stale remainder so a reversal answers on the first event, and the mouse-reporting path emits one report per whole notch capped at 12/event rather than one per micro-event (offered upstream as #30). The tail is followed only when the view is already on it — upstream called ScrollToBottom() after every chunk, so anything printing yanked a scrolled-back reader down — sampling IsAtBottom BEFORE the write, and the scrollback ring's Trimmed event moves a parked viewport down by the evicted count so content stays under the reader's eye. AutoScrollToBottom (styled, default true) opts out entirely, with IsFollowingTail and a public FollowTail() to observe and drive follow state; property shape adopted from upstream #25.
  
  Keyboard: Shift+arrows/Home/End extend a buffer selection rather than sending ESC[1;2C and friends, which no interactive shell binds (zsh echoes the ";2C" tail into the command line); Alt/Ctrl+Left/Right send ESC-b/ESC-f, which zsh, readline, fish and PSReadLine all bind out of the box; paste moves to the platform's own chord — Cmd+V on macOS, Ctrl+V elsewhere — alongside Ctrl+Shift+V, which is a deliberate BEHAVIOURAL DIFFERENCE from upstream, where Ctrl+V is left to the running program for literal-input mode; other Cmd/Super chords bubble to the host instead of typing a literal letter into the PTY; CapsLock counts as a modifier.
  
  Rendering: inverse video no longer requires an opaque Background — with Background=Transparent the inverse/DECSCNM/blink swap put a transparent brush in the FOREGROUND slot and drew SGR 7 text as an unreadable block — and the cursor is not painted when no process is attached, where upstream drew it from buffer state alone and left a stray caret at (0,0) on a view that never launched.
  
  Host integration: ShellReady (adopted from upstream #27) fires once on the shell's first output, and the same latch forces a real paint on that chunk because upstream's first paint is focus-gated and frame-throttled, leaving a freshly launched terminal blank until clicked; Refresh() re-applies metrics, re-grids, drops render caches and invalidates immediately; OutputReceived raises every decoded PTY chunk on the read task, exception-guarded so a subscriber cannot kill the read loop (same member as upstream #24, which marshals to the UI thread instead); AttachConnection(IPtyConnection) binds the view to a PTY someone else owns, with CleanupProcess skipping the kill for an attached connection so closing a viewer pane never takes the process down; EnvironmentOverrides merges extra environment over PtyOptions.Environment at launch; and the dormant-view primitives IsLive, SendAsync, CurrentLineText, ClearScreen(), CharWidth/CharHeight and SuppressCursor let a host own a session's lifecycle. TerminalControl forwards the new events and proxies AutoScrollToBottom.
  
  Adds the test project upstream never had: seven headless tests driving the real input path (window, hit test, routed event) rather than calling handlers, covering wheel notches, trackpad accumulation, wheel over blank space, direction reversal, follow-tail state, scrollback trimming and the AutoScrollToBottom gate.
  
  Three changes carried in the pre-fork vendored copy turned out to be already merged upstream and are NOT part of this divergence: the EOF ProcessExited fallback (#18), modifier keys no longer clearing the selection (#21), and null-safe Kill()/WaitForExit()/ExitCode (#22).
  
