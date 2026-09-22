# Timer and stopwatch clocks

## Existing behavior retained by the clock-accuracy fix

- Running timers and stopwatches include Windows sleep/hibernate time. A delayed tick processes a countdown's completion once; it does not create one reflection for every missed repeat. An enabled repeat starts a new, full session when processing resumes.
- Hiding a window or closing it to the tray does not pause a session. Fully quitting or rebooting leaves a running session recoverable; reopening includes the time away using the saved calendar timestamps.
- Paused sessions and the parked mode do not accrue time. Stopwatch review pauses before opening the reflection; saving that draft resumes only its own still-current stopwatch. Reflection-writing time is excluded while paused.
- Scheduled starts and auto-start cutoffs use the PC's calendar clock. A clock correction may make an appointment due, but must not invent elapsed work in the session it interrupts.
- Reflections and spreadsheet dates use the local calendar time at submission. Previously saved reflection totals are not recalculated.

## Implementation contract

Active elapsed time uses an injectable monotonic source, separate from calendar time. Runtime timer deadlines and running-since values must be compared with `TimerEngine.ElapsedNow`, not `Now`. Convert a runtime deadline with `CalendarTimestamp` only for calendar display; never serialize a raw runtime clock coordinate as a recovery timestamp.

Each durable save writes portable calendar recovery timestamps plus accumulated stopwatch milliseconds or precise countdown milliseconds remaining. A failed write changes neither the visible session nor its timing basis. Checkpoints are part of the existing encrypted state/backup transaction, not a second settings file. Legacy version-1 state remains readable.

After a full app restart, elapsed time during downtime is necessarily a calendar-based estimate. The app reports this when recovering a running session. It cannot distinguish a forward clock correction while closed from genuine downtime. If the clock is earlier than a recorded checkpoint, recovery retains that checkpoint's accumulated work and does not invent downtime; a specific notice explains the uncertainty. For legacy data without a checkpoint, only the old saved timing information is available.

The production Windows elapsed source is .NET `TimeProvider.System` / `Stopwatch`. Windows performance-counter time includes standby and hibernation, preserving the existing sleep policy: [Microsoft high-resolution timestamp guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps), [.NET TimeProvider](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview).

The calendar delegate-only constructor remains a deterministic compatibility seam for older tests. Clock-correction tests must inject both calendar and monotonic time via `TimeProvider`; production uses the system provider.
