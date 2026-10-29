# CLAUDE.md — MidiControl

Guidance for working on `src/MidiControl`; the repository-wide rules (tests for every change, one calculation one
function, documentation, HA config) are in the root `CLAUDE.md`.

Side project, low priority. The only plugin with ImageSharp (in its own folder). Talks to the controller through HA
(`mqtt.publish`, `mqtt_event`), no MQTT client of its own. Its fonts and icons still come from absolute paths
(`RegularFontPath`, `LightFontPath`, `IconPath`, defaults under `/config/netdaemon5/`): move them into its plugin
folder (`AppFiles.PathOf`) when MIDI is revived. No unit tests yet — add `tests/MidiControl.Tests` with the first
testable logic.
