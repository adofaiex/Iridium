## CHANGES

### Performance

1. Some of the heavier calculations now run on a new Rust core. It is open source and contains no code from the game itself. If the native library fails to load, Iridium falls back to the original code path, so compatibility is not a concern.

### Fixes

2. Fixed the in-game mod list under UMM not showing the Iridium page. After updating, you will see a short in-game hint (including the panel hotkey) instead of wondering where the settings went.

### Languages

3. Merged PR #24, adding Latin American Spanish.
