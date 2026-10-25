![Iridium](https://socialify.git.ci/Xbodwf/Iridium/image?custom_description=An+optimized+mod+for+ADOFAI&custom_language=csharp&description=1&font=Lexend&forks=1&issues=1&language=1&name=1&pulls=1&stargazers=1&theme=Auto)

# Iridium

一个专注于性能优化、视觉调整和兼容性的 A Dance of Fire and Ice 优化 Mod

[![许可证: LGPL v3](https://img.shields.io/badge/License-LGPL%20v3-blue.svg)](LICENSE)

[English](README.md)

欢迎加入我们的Discord服务器!

https://discord.gg/ddndY4xXeK

---

> [!IMPORTANT]
> Iridium 旨在通过极致的性能优化和现代化的视觉调整来提升您的《冰与火之舞》体验。

## 版本支持

- **v2 分支**：适用于 ADOFAI v2
- **v3 分支**：适用于 ADOFAI v3

---

## 功能

### 性能优化

让游戏运行更加流畅，减少卡顿和掉帧。主要体现在画面的渲染效率提升、特效性能改善以及场景加载速度加快。包括装饰纹理压缩、装饰物分帧加载（带进度显示）、移动轨道/移动装饰物优化（支持 freeroam 区域）、粒子优化（对象池/剔除/LOD）、DOTween 调优、自定义缓速引擎，以及高精度计时的异步输入优化。

### 原生核心（Rust）

最热的几段循环现在交给一个小型 Rust 库（`native/iridium-core`，C ABI）执行，不再跑在 Mono 上。目前覆盖六条热路径：

| 位置 | 原生入口 | 实测提升 |
| --- | --- | --- |
| 谱面 JSON 解析 | `iridium_core_json_*` | 3 万事件谱面 477ms → 82ms |
| 缓动求值 | `iridium_core_evaluate_batch` | 15 万砖基准 3.7x |
| 编辑器砖块路径重建 | `iridium_core_remake_path_from` | 支持全量重建与从锚点增量续算 |
| 装饰物合批顶点 | `iridium_core_parallax_compute` | 脏标记让未变化的槽位跳过 `Mesh.SetVertices` |
| 装饰物命中查询 | `iridium_core_spatial_*` | AABB 粗筛跳过无候选的物理查询 |
| 纹理降采样 | `iridium_core_resize_bilinear` | 双线性降采样 1.7x–2.1x |

这个库完全是可选的。它通过 `dlopen`/`LoadLibraryEx` 加载，并经过 ABI 版本探测；库缺失或版本过旧时，每个调用点都会自动回退到原来的托管实现。FFI 边界会捕获 panic 并转成错误码，所以原生代码出问题时只是掉性能，不会让游戏崩溃。逐位一致性由 Rust 测试套件保证，CI 每次发布前都会跑一遍。

### 界面自定义

提供多种界面调整选项，包括移除首页新闻、隐藏测试版水印、调整自动播放文字的位置、在编辑器中也显示倒计时等。v3 的设置界面分为 通用 / 优化 / 编辑器 / 兼容性 / 音频 选项卡，Switch 与 Checkbox 按语义区分，并支持中文/日文/韩文输入与显示（CJK 字体回退）。

### 大厅音乐替换

支持在不同转速下切换不同的背景音乐，也可以使用自定义音乐文件。

### 判定文字自定义

可以自由修改游戏中的判定文字内容（如"完美"、"过早"等），支持富文本标签，也可以切换为显示输入偏移毫秒数。

### 打击音

打击音的音高会跟随音乐音高同步变化。

### 编辑器优化

从多个方面改善编辑器的使用体验：对大型谱面（上万砖块）的插入和删除操作进行性能优化；支持自定义快捷键用于快速操作装饰物和砖块；可在自动播放预览中使用暂停/继续功能。

### 第三方 Mod 兼容
- **忽略需要的第三方 Mod**：无视谱面声明的第三方 Mod 依赖直接打开并游玩；保存谱面时 requiredMods 完整保留，加载完成后会提示缺失的 Mod。
- **第三方自定义事件**：未知事件类型（CustomEvent）会被临时注册，谱面可正常加载；编辑器内以只读面板 + 独立选项卡展示，保存后事件数据不丢失。

### 兼容性与问题修复

针对旧版谱面提供行为兼容选项（如旧版闪烁和摄像机相对模式），同时修复了游戏本身存在的一些问题，包括传送卡死、发卡弯节拍检测、编辑器播放失误重置等。

### 补丁模式

提供 IL Transpiler 和 Prefix/Postfix 两种补丁模式，用户可根据自身需求在性能与兼容性之间选择。

---

## 安装方法

根据你的 Mod 加载器选择对应的安装指南：

- [UnityModManager](docs/loader/umm_zh-CN.md)
- [MelonLoader](docs/loader/melonloader_zh-CN.md)
- [BepInEx](docs/loader/bepinex_zh-CN.md)

> [!CAUTION]
> 除非是维护者推出的针对旧版游戏的特调版本，否则请勿在 ADOFAI **2.9.7 及以下**版本运行 Iridium。我们不保障此情况下的功能稳定性和兼容性。

---

## 自行构建

1. 确保已安装 .NET SDK。Rust 工具链是可选的，只有想自己编译原生核心时才需要，不装也能正常构建。
2. 带子module克隆本仓库：
   ```bash
   git clone --resursive https://github.com/Xbodwf/Iridium.git
   cd Iridium
   ```
3. 在 `Iridium.csproj` 中设置游戏目录路径。
4. 使用 dotnet 构建并部署：
   ```bash
   dotnet build
   ```

### 构建原生核心

`scripts/build-native.sh` 会编译 `iridium-core` 并把产物暂存到 `out/native/`，csproj 会自动取用并打进发布包。正式发布的版本已经包含三平台的 `.dll`、`.so` 和 `.dylib`，本地做迭代时才需要这一步。

```bash
# 构建当前平台的原生库
bash scripts/build-native.sh

# 跑测试，以及 CI 在发布前会跑的逐位一致性套件
cd native/iridium-core && cargo test --release
```

macOS 上加 `--universal` 会顺带编译另一种 CPU 架构，再用 lipo 合并成单个双架构 dylib，arm64 和 Intel（Rosetta）都能用：

```bash
bash scripts/build-native.sh --universal
```

想跑性能基准的话需要 `cargo bench`：

```bash
cd native/iridium-core && cargo bench
```

---

## 致谢

感谢所有贡献者的支持：

<a href="https://github.com/Xbodwf/Iridium/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=Xbodwf/Iridium&max=200&columns=14" />
</a>

> 完整贡献者名单见 [contributors.md](contributors.md)
