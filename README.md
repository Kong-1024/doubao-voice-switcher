# 豆包语音切换器

Windows 下让微信输入法继续作为日常主力输入法，同时用豆包输入法提供高准确率的语音输入。

## 交互

| 操作 | 结果 |
| --- | --- |
| 单击物理右 Alt | 切换到豆包并启动免按语音识别 |
| 再次单击物理右 Alt | 结束识别、提交文字并恢复微信输入法 |
| 按 Esc | 取消本次语音并恢复微信输入法 |

右 Alt 的按下和松开都会被工具拦截，因此不会打开应用菜单，也不会依赖豆包的长按快捷键。

## 工作方式

程序使用全局低级键盘钩子捕获真实右 Alt，再通过 Windows TSF 激活输入法：

1. 优先精准激活豆包输入法，失败时使用 Win+Space 兜底；
2. 调用豆包 `rpc.dll` 的本地 RPC，按官方时序发送“预启动录音 → 显示免按波形”；
3. 第二次右 Alt 发送停止消息，等待文字提交后激活微信输入法。

当前豆包版本使用的 RPC 消息为 `0x3EF`（预启动）、`0x3F4`（显示语音条）、`0x3F0`（停止）和 `0x3F5`（取消）。这些内部接口可能随豆包版本变化，升级豆包后应重新运行自检。

## 环境要求

- Windows 10/11 x64
- .NET 9 SDK（仅源码编译需要）
- 已安装并启用微信输入法、豆包输入法
- 豆包输入法当前版本目录中存在 `rpc.dll`

项目已针对豆包输入法 `v0.9.0.0` 验证。

## 使用已发布程序

进入 `豆包语音切换器` 文件夹，双击 `DoubaoVoiceSwitcher.exe`。程序会常驻系统托盘。

程序默认不修改开机启动；如需开机运行，可在托盘菜单中勾选“开机自动启动”。

## 从源码构建

```powershell
dotnet build .\DoubaoVoiceSwitcher\DoubaoVoiceSwitcher.csproj -c Release --no-restore
dotnet publish .\DoubaoVoiceSwitcher\DoubaoVoiceSwitcher.csproj `
  -c Release -r win-x64 --self-contained false
```

发布输出为框架依赖版本，需要目标电脑安装 .NET 9 Desktop Runtime。也可以在项目文件中将 `SelfContained` 改为 `true` 后发布自包含版本。

## 自检

关闭正在运行的主程序后执行：

```powershell
& ".\豆包语音切换器\DoubaoVoiceSwitcher.exe" --rpc-probe
```

自检会自动验证：切换到豆包、预启动录音、显示语音条、停止语音和恢复微信输入法。结果写入程序目录的 `test-result.txt`。

## 配置

配置文件为程序目录下的 `settings.json`：

- `activationDelayMs`：输入法激活等待上限，默认 1500ms；
- `commitDelayMs`：停止后等待文字提交，默认 300ms；
- `maxListeningSeconds`：最长连续录音时间，默认 60 秒；
- `playSoundFeedback`：是否播放开始/结束提示音，默认关闭；
- `cancelOnEsc`：是否允许 Esc 取消。

## 注意事项

- 这是针对本机输入法安装状态的 Windows 工具，不是独立的语音识别服务。
- 豆包输入法或其 RPC 接口升级后，语音消息编号可能变化；遇到启动失败应先运行 `--rpc-probe`。
- 程序只提交语音结果到当前前台输入框，不负责保存或上传录音数据。

## License

本项目暂未声明开源许可证，默认保留作者权利。如需公开复用，请先补充合适的 License。
