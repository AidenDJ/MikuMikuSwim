# 0.1.12：PDA 与过场兼容性

## PDA 图标

`MMS_pda_icon.png` 是独立的 128×128 RGBA 图标，保留原有 `MMS_logo.png` 作为旧版回退。加载时保留生成的透明度，不再扫描大图、裁边和逐像素缩放。

参考来自本机游戏 `Subnautica_Data/StreamingAssets/aa/StandaloneWindows64/atlases_1bb3f2049945bd4742384fe4c0855ca3.bundle` 中的 `Tabs` 图集。提取了 TabInventory、TabJournal、TabEncyclopedia、TabTimeCapsule、TabLog、TabPing、TabGallery 共七个 Sprite，以及图集纹理。可使用 `python tools/extract_pda_icons.py` 重新提取到本地 `build/pda-012`；原版素材不随模组发布。

新图使用内置 image_gen 生成，原版图集作为风格参考。生产资产只做等比例缩小，保留 alpha。最终提示词：

> Create ONE new transparent PNG game UI tab icon for a Subnautica player model replacement mod called MikuMikuSwim. Reference image is the ACTUAL extracted original game PDA tab icon atlas, for STYLE ONLY. Match especially the person inventory icon: simple solid pale icy cyan / near white upper part gently graduating to sky cyan at bottom, extremely subtle cyan halo, soft rounded silhouette, clear at 40 pixels. New subject: a front facing faceless female avatar head and shoulder bust with two short recognizable twin tails, one on each side, rounded and minimal. Same simple symbol language and visual weight as the atlas person icon. Centered on square transparent canvas, silhouette occupies about 64 percent of canvas width and height leaving space for small soft cyan halo. No face, eyes, text, letters, headset, outlines, borders, UI panels, tiles, background, water scene, sparkles, or decorative details. This is a single standalone icon, not a sheet, with genuine alpha transparency.

## PDA 操作开销

- 模型按钮按目录数量复用，切换选中项更新文字和颜色。
- 材质行按使用过的最大数量复用，重新绑定材质名与开关状态；设置状态不会触发修改事件。多余行停用，行内文字改为限定宽度，移除每行独立的裁剪组件。
- 同一代模型、同一组材质和状态不重复刷新材质行。
- 静态预览仍按需绘制；连续拖动、缩放、参数修改合并为最多 45 次/秒，最后一次修改保持待绘制状态。只限制预览副摄像机，不限制游戏或 PDA 控件帧率。
- 预览高度按界面尺寸取 320–640，替代固定 720。首次打开的同步像素读回改为仅在详细日志模式执行。

## 过场

- 过场中暂停模组镜头偏移。只还原仍由模组持有的位置和旋转，保留游戏刚写入的动画镜头。
- 距离头部 0.8 米以内的过场镜头，其旋转映射到模型头部绑定坐标，包含侧倾；替换头部朝向，避免和原动画重复叠加。外部远景保持源骨架动画。
- 过场头发粒子的位置和前一帧位置同时随挂点刚性移动、旋转，不把镜头歪头误当作水中惯性。普通游泳仍使用原物理行为。
- 第一人称头部隐藏范围包含摄像机近裁剪面；近距离过场隐藏可能穿过镜头的头发。过场近裁剪面进入已有躯干碰撞体时，身体暂时仅投影，离开或退出过场后恢复。没有修改整个模型的透明材质。

## 验证范围

以安装游戏的程序集构建通过；仅有原来四个未使用字段警告。离线检查了任意绑定坐标下的镜头侧倾映射、头发刚性移动前后的长度和速度守恒、近裁剪角点覆盖、30/60/120/144 FPS 下的预览调度。坐标误差小于 3×10⁻¹⁶；十秒预览绘制次数分别为 300/450/450/450。

尚未启动或操作游戏。实际 PDA 帧耗时、过场穿模与不同模型的头发外观，需要用户在游戏内验证，不能从这些离线检查推断已经完全修复。
