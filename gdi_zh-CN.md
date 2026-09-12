# 安装 libgdiplus（Linux / macOS / BSD）

Iridium 在两种场景下使用 Mono 的 `System.Drawing`：

- 绘制设置界面（图标、按钮、开关）；
- 启用图像优化选项时，压缩装饰物/贴图。

两者都依赖原生的 **libgdiplus** 库。Windows 自带该库；其他系统通常需要手动安装。

如果 Iridium 设置面板无法打开、界面缺少图标/按钮，或装饰物/贴图压缩不生效，
请按下方对应系统的命令安装 libgdiplus，然后重启游戏。

## Linux

### Debian / Ubuntu / Linux Mint / Pop!_OS / SteamOS 2

```bash
sudo apt update
sudo apt install libgdiplus
```

### Arch Linux / Manjaro / EndeavourOS / SteamOS（Steam Deck）

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

在 `configuration.nix` 中加入：

```nix
environment.systemPackages = [ pkgs.libgdiplus ];
```

如果使用 Steam 包，还需要让 Steam 能找到该库：

```nix
programs.steam.package = pkgs.steam.override {
  extraPkgs = pkgs: [ pkgs.libgdiplus ];
};
```

然后执行 `sudo nixos-rebuild switch`。

## macOS

Homebrew：

```bash
brew install mono-libgdiplus
```

## FreeBSD

```bash
sudo pkg install libgdiplus
```

或从 ports 安装：

```bash
cd /usr/ports/x11-toolkits/libgdiplus && sudo make install clean
```

## 检查是否安装成功

Linux：

```bash
ldconfig -p | grep libgdiplus
```

macOS（Homebrew）：

```bash
brew list mono-libgdiplus
```

## 沙箱环境注意事项

- 如果 Steam 以容器方式运行游戏（Steam Linux Runtime / Flatpak），库必须在该环境**内部**可用。
  对于普通的 Linux 原生版本，在宿主机安装即可。
- 如果你使用的是 Flatpak 版 Steam，宿主机安装的包在沙箱内不可见 —— 若面板仍无法加载，
  请改用非沙箱方式安装的 Steam。
- Steam Deck 的系统分区是只读的，手动安装的系统包可能在系统更新后被清除。
