# MidiControl
Integrates a BCF2000 MIDI controller with Home Assistant — maps faders/buttons to HA entities and renders live state back to the controller's display. Side project, low priority.

## Build, run

A NetDaemon app plugin (`src/AppPlugin.props`), loaded by the host from `apps/plugins/MidiControl/`; the only one that
needs ImageSharp (in its folder). Its fonts and icons are read from absolute paths (`RegularFontPath`, `LightFontPath`,
`IconPath`, defaults under `/config/netdaemon5/`). No unit tests.
