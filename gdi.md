# Installing libgdiplus (Linux / macOS / BSD)

Iridium uses Mono's `System.Drawing` for two things:

- drawing the settings UI (icons, buttons, switches), and
- compressing decorations/textures when the image optimization options are enabled.

Both need the native **libgdiplus** library. On Windows it ships with the runtime; on other systems
you usually have to install it yourself.

If the Iridium settings panel fails to open, icons/buttons are missing, or image/decoration compression
does not work, install libgdiplus with the command for your system below and restart the game.

## Linux

### Debian / Ubuntu / Linux Mint / Pop!_OS / SteamOS 2

```bash
sudo apt update
sudo apt install libgdiplus
```

### Arch Linux / Manjaro / EndeavourOS / SteamOS (Steam Deck)

```bash
sudo pacman -S libgdiplus
```

### Fedora

```bash
sudo dnf install libgdiplus
```

### RHEL / CentOS / Rocky Linux / AlmaLinux

```bash
sudo dnf install epel-release
sudo dnf install libgdiplus
```

### openSUSE (Leap / Tumbleweed)

```bash
sudo zypper install libgdiplus
```

### Alpine Linux

```bash
sudo apk add libgdiplus
```

### Void Linux

```bash
sudo xbps-install libgdiplus
```

### Gentoo

```bash
sudo emerge --ask x11-libs/libgdiplus
```

### NixOS

Add it to `configuration.nix`:

```nix
environment.systemPackages = [ pkgs.libgdiplus ];
```

If you use the Steam package, also make the library available to Steam:

```nix
programs.steam.package = pkgs.steam.override {
  extraPkgs = pkgs: [ pkgs.libgdiplus ];
};
```

Then rebuild with `sudo nixos-rebuild switch`.

## macOS

Homebrew:

```bash
brew install mono-libgdiplus
```

## FreeBSD

```bash
sudo pkg install libgdiplus
```

Or from ports:

```bash
cd /usr/ports/x11-toolkits/libgdiplus && sudo make install clean
```

## Check whether it is installed

Linux:

```bash
ldconfig -p | grep libgdiplus
```

macOS (Homebrew):

```bash
brew list mono-libgdiplus
```

## Notes for sandboxed setups

- If Steam runs the game inside a container (Steam Linux Runtime / Flatpak), the library must be available
  **inside** that environment. Installing it on the host works for the normal native Linux build.
- If you use Flatpak Steam, host packages are not visible inside the sandbox — use a non-sandboxed Steam
  installation if the panel still fails to load.
- On Steam Deck the system partition is read-only, and custom system packages may be removed by system updates.
