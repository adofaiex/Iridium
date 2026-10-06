# GitHub Actions Workflow

## Build and Release Workflow

This workflow automatically builds and releases the Iridium mod when changes are pushed to the main branch.

### Features

- **Automatic Build**: Builds the .NET project using the same process as `build.sh`
- **Native Rust Library**: Builds `iridium-core` for all three platforms — `libiridium_core.so` + cross-compiled `iridium_core.dll` (mingw-w64) on the Linux job, and a universal `libiridium_core.dylib` (arm64 + x86_64) from a dedicated `native-macos` job — packages all three into one zip (the loader picks per OS at runtime), then runs the bit-exactness test suite; without these steps `out/` would be empty on a clean checkout and every hot path would silently fall back to managed code
- **Version Detection**: Automatically extracts version information from `Info.json` and `VersionManager.cs`
- **Artifact Packaging**: Creates a zip file with the build output
- **Release Management**: Creates GitHub releases with the built artifacts
- **Duplicate Handling**: If a release with the same name already exists, it appends the commit SHA to the release title

### Trigger Conditions

- Pushes to the `main` branch
- Tags starting with `v*`
- Pull requests to the `main` branch (build only, no release)

### Workflow Steps

1. **Build Job** (Windows environment):
   - Checkout repository
   - Setup .NET SDK
   - Setup Python
   - Build project with `dotnet build`
   - Extract version information
   - Package artifacts as zip file
   - Upload build artifacts

2. **Release Job** (only on push to main):
   - Download build artifacts
   - Check for existing releases with same name
   - Create new release or update existing one
   - Upload zip file as release asset

### Version Format

The workflow uses the following version format:
- Release builds: `{Version from Info.json}`
- Non-release builds: `{Version}_{type}{minor}` (e.g., `1.0.5_beta1`)

If a release with the same name exists, the SHA is appended: `{Release Name} ({short-sha})`
