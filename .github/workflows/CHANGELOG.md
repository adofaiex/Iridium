> [!NOTE]
> Iridium supports multiple mod loaders.See README.md for platform installation.

> [!WARNING]
> Linux/macOS requires libgdiplus for UI rendering and image compression. See gdi.md to installation.

## CHANGES

1. 在 2.9.8 版本上支持加载、编辑与保存 ADOFAI 3.x 谱面（v3 专属属性会被保留）。
2. 编辑器：大谱面下插入/删除砖块与切换砖块大幅提速。
3. 加载：大谱面加载更快（特效与线号按需生成、砖块分帧激活）。
4. 超大谱面的内存占用降低。
5. 渲染分辨率可调——稍微调低一点即可换取更高帧率（v2 / v3）。
6. 支持 MiniModLoader（第三方 Mod 加载器）。
7. 装饰物判定更快，并支持播放时装饰物缩放。

> [!CAUTION]
> Disclaimer: Optimization mods are not a silver bullet — do not chase FPS blindly. If issues occur, disable the specific feature and report the bug instead of labeling the entire mod broken.
