# Changelog

## 0.3.3

- **Games launched from Steam (or any other app MixDeck already captures) had no sound.** A process was skipped when one of its ancestors was being captured, assuming the ancestor's loopback tree covers it; it doesn't for processes started later. Now only helper processes of the same app are skipped (e.g. browser tabs), so a game gets its own capture and its own channel.

## 0.3.2

- **Idle CPU down from ~25% of a core to under 2%.** The device poll read every endpoint's friendly name each 1.5 s; NAudio walks the whole property store for that (~40–50 ms per device), about 500 ms of CPU per poll. Names are now cached per device id.
- Handle count stays flat instead of climbing into the thousands: the COM objects created by each poll are released promptly.
- Level meters only animate while the window or the tray mixer is visible.
- RAM in the tray: ~180 MB instead of ~280 MB.
- Fixed a rare error when a second launch arrived while MixDeck was closing.
- `MIXDECK_DEBUG=1` now also logs CPU time per poll stage.

## 0.3.1

- Level meters never extend past the slider's volume setting (the meter uses a dB scale, the slider a linear one).
- Fixed the "Switch on while running" label overlapping its text box in profile settings.
- New installs check this repository for updates by default.

## 0.3.0 — first public release

**Mixing**
- Channels × outputs: every channel has its own volume and mute on every output, plus a mute-everywhere switch.
- Apps appear as chips with their icons; drag them between channels or right-click → Move to. Click a chip for the app's own volume.
- Apps are captured individually with WASAPI process loopback from a silent collector device — no virtual audio driver.
- Outputs play in the device's native format (including 8-channel "7.1" gaming headsets), with fallback devices when one is unplugged.
- Smooth clock-drift correction between capture and output devices (no pitch shift, no dropouts).

**Sound**
- Auto-ducking: chosen channels get quieter while another one (e.g. Chat) is active.
- Per-output EQ (bass / mid / treble), AutoEQ headphone correction, hearing-protection limiter.
- Microphone channels with RNNoise noise suppression, rumble filter, noise gate, compressor and gain.

**Everyday use**
- Tray mini mixer, first-run setup wizard, global hotkeys with an on-screen popup.
- Profiles that switch on automatically while a chosen app runs.
- Guards the Windows default output; restores it on exit, with a crash watchdog as a backup.
- Warning and one-click fix for apps that play past the mixer.

**Remote**
- Phone remote via QR code, token-protected local HTTP + WebSocket API, Home Assistant and Stream Deck examples.

**Install and updates**
- Per-user installer (no admin) that installs .NET 10 if needed; in-app updates from GitHub Releases.
