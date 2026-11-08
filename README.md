# 破烂优化

Windows 诊断与优化工具：**诊断全程只读，改动能还原**。

主界面 9 个分组、74 张功能卡片，全部在软件内完成，不调用 `msc` / `cpl` 等外部管理单元。

<p align="center"><img src="docs/screenshot.png" width="720" alt="主界面" /></p>

## 功能分组

| 分组 | 代表功能 |
|---|---|
| 系统信息 | 精简系统识别、硬件清单、DirectX / 运行库 |
| 系统优化 | 92 项可还原优化（服务 / 电源 / 计划任务 / 视觉） |
| 游戏优化 | 着色器缓存、游戏模式、GPU 首选项、直播伴侣优化 |
| 网络修复 | 断网急救 16 步、DNS 优选（真实测延迟）、防火墙清单 |
| 性能与进程 | 任务管理器 / 内存构成与占用排行 / 句柄排查 |
| 显示配色 | ICC 导入导出、手动调色、DLSS 预设 |
| 硬件取证 | 外挂痕迹体检、深度硬件痕迹（DMA 检测）、程序执行记录 |
| 清理与空间 | C 盘清理、大文件、重复文件（内容比对）、注册表孤儿项 |

## 设计原则

- **诊断只读**：不加载驱动、不注入进程、不联网上传本机数据
- **改动能还原**：每一类系统修改（服务 / 电源 / MMCSS / GPU / DNS）各有独立备份与还原入口
- **删除先给清单**：C 盘清理、大文件、重复文件逐项勾选确认，且只进回收站
- **代价写明**：每个动作的确认框都写清它做什么、不做什么、风险在哪
- **失败降级**：某一路系统数据读不到（精简系统常见）只影响对应显示，不拖垮整体

## 下载与运行

见 [Releases](../../releases)：单文件 exe（约 65 MB，内置 .NET 8 运行时），无需安装。

- 系统：Windows 10 / 11，64 位
- 权限：诊断类功能无需管理员；修改系统的项会提示提权重启
- 杀毒软件可能因「未签名 + 行为特征」误报——软件不联网上传数据、不加载驱动，详见软件内右上角「关于」

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）：

```bash
# 1.（可选）生成 exe 图标——图标是生成产物，不进版本库
python tools/make-ico.py src/SysDiag/app.ico

# 2. 构建（单文件自包含）
dotnet publish src/SysDiag/SysDiag.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o dist

# 3. 自检（496 项，全部在软件内运行）
dist/SysDiag.exe --selftest
```

## 自检与布局体检

- `--selftest`：496 项断言，覆盖每个功能的判定口径、确认框文案、恢复链路
- `--layout-audit`：38 个窗口自动体检（元素越界 / 文本放不下），支持 `--screen=1366x728` 模拟小屏
- `--notice-probe`：公告拉取与验签自证

## 许可

[Apache License 2.0](LICENSE)。第三方组件与素材清单见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
