# Bugle Hero

Turn your in-game bugle into a **MIDI-powered instrument**.  
Play real MIDI notes in PEAK and toot along with your friends in multiplayer.

[![Bugle Hero Showcase](https://img.youtube.com/vi/8ZpSbefGZDA/0.jpg)](https://youtu.be/8ZpSbefGZDA)  
▶️ [Watch on YouTube](https://youtu.be/8ZpSbefGZDA)

---

## Features
- Control the bugle with any connected MIDI input (keyboard, controller, player, etc.)
- Notes are mapped into the bugle’s pitch range automatically
- Works in both local and multiplayer sessions
- Selectable MIDI device through config file
- Built-in safeguards against stuck notes

---

## Installation
1. Install [BepInEx Pack for PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/)  
2. Download **Bugle Hero** from [Thunderstore](https://thunderstore.io/c/peak/) or from [Releases](https://github.com/Grinjr/BugleHero/releases)  
3. Extract the contents into:
   ```
   PEAK/BepInEx/plugins
   ```
4. Launch the game. BepInEx will generate a config file at:
   ```
   PEAK/BepInEx/config/BugleHero.cfg
   ```

---

## Usage
- Equip the bugle in PEAK
- Play notes on your connected MIDI device  
- Only one note plays at a time (most recent note overrides previous)  
- If auto-detection picks the wrong device, edit your config:
  ```ini
  [MIDI]
  DeviceName = Your Device Name Here
  ```

---

## Configuration
| Setting                        | Default      | Description |
|--------------------------------|--------------|-------------|
| `MIDI.DeviceName`              | Auto-detected | MIDI device to use for input |

---

## Limitations
- ❌ Linux support is not implemented (Windows only)  
- ❌ No polyphonic playback (single note at a time)  
- ⚠️ Multiple MIDI devices may cause unpredictable behavior  

---

## Technical Details
Bugle Hero works by patching PEAK’s bugle logic with **Harmony**:
- Hooks into the bugle’s pitch-handling methods to replace them with live MIDI input.  
- Uses **NAudio** to capture MIDI note on/off messages from any connected device.  
- Implements a simple **stuck-note safeguard**: if a note-off message is missed, the bugle is automatically reset on new input.  

---

## Development Status
This project started as a quick prototype (~8 hours). It’s still **early in development** and may have bugs. Contributions, testing, and feedback are welcome!

---

## Contributing
- Found a bug? [Open an issue](https://github.com/Grinjr/BugleHero/issues)  
- Want to contribute code? Fork the repo, make your changes, and open a pull request.  

---

## License
GNUv3 License – see [LICENSE](LICENSE) for details.  
