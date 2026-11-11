# Availability across multiple DWGs

`cad_runtime_status.document_availability` reports the pinned DWG's document_id, read_available, write_available, command_in_progress, reason, and observed_at. It uses a snapshot without waiting for the CAD UI thread. This is the last observation, not a reservation; the worker checks again before execution.

The queue preserves order within each document and schedules ready requests from other DWGs around a busy one. An idle inactive document can be read without tab activation. Writes run when the target is idle and AutoCAD permits activation. BACTION in one drawing no longer unconditionally blocks every request.

Known unavailability rejects a write before acceptance with DOCUMENT_BUSY. The agent checks availability and fresh context. The panel reads fast status first and shows the active command instead of waiting to read a busy DWG. Expired/cancelled requests do not hold up ready requests.

Scheduling switches at safe boundaries: AutoCAD API calls remain sequential on the main thread. Modal dialogs, running Lisp, blocked MDI activation, and long kernel calls can suspend CAD requests throughout the process. The worker never sends Esc or interrupts a user command. Models, host compilation, downloads, and PDF reading may run concurrently; simultaneous CAD kernel calls require separate AutoCAD processes.
