# SJTU Link

一个用于上海交通大学**学生 IKEv2 VPN** 的轻量 Windows 桌面客户端。使用 Windows 内置 VPN 引擎和系统登录窗口，无需 aTrust 运行。

个人开源项目，非校方产品。Windows 10/11 x64；运行依赖系统自带的 .NET Framework 和 Windows PowerShell 5.1。

## 功能

- 指定目标分流，或使用默认 VPN 路由。
- IPv4 / IPv6 地址、网段和准确域名的规则预览。
- 系统登录、连接状态、断开连接和规则草稿。
- 查询目标的 Windows IP 路由选择，提示系统代理及其他 VPN 的影响。
- 仅修改本工具创建的 VPN 配置；应用失败时尝试恢复原配置。

## 使用

从仓库的 Releases 下载 Windows 压缩包，解压后运行 `SJTU-Link.exe`。

1. 如果 aTrust 已连接，先在其中注销。
2. 学生服务器选择 `stu.vpn.sjtu.edu.cn`；IPv4 备用入口为 `stuv4.vpn.sjtu.edu.cn`。
3. 选择模式，编辑需要走 VPN 的目标。每行一个准确域名、IP 或 CIDR 网段。
4. 点击**预览规则 → 应用配置 → 登录并连接**。首次使用必须先应用配置。
5. 在 Windows 登录窗口输入自己的 jAccount 用户名和密码，**“域”一般留空**。

默认规则 `net.sjtu.edu.cn` 仅用于测试，不包含完整校园资源列表。修改已应用规则时，需要断开、重新预览、应用并连接。

## 从旧版本升级

关闭旧窗口，解压新版并运行。v1.0.2 基于使用者审查通过的候选版本，替代此前已撤回的同名发布；如曾下载旧 v1.0.2，请重新下载。

管理标记异常时，可在应用配置时确认恢复，也可在“使用说明”页点击“恢复旧连接管理”。恢复只更新本机管理记录，不重建连接、不重置认证或路由。接管前会核对学生服务器和 IKEv2/EAP 类型。

草稿尚未应用时，登录可明确选择沿用已生效配置，不会自动应用草稿。原先间歇性标记失配的具体触发条件仍未复现，本版提供诊断与已验证的恢复路径。

## 分流的边界

| 模式 | 行为 |
| --- | --- |
| 指定目标分流 | 配置的目标进入 VPN，其他目标沿用系统已有路由。 |
| 默认走 VPN | Windows IPv4 默认隧道和 `2000::/3` 全球单播 IPv6 路由；本地网络及更具体的路由仍可能优先。 |
| 域名规则 | 预览时解析为 IP 路由；不自动包含子域名，不跟踪 DNS/CDN 地址变化。共享 IP 的其他网站可能一并受影响。 |

本工具未实现按进程分流、通配域名、独立 DNS 服务或断网保护。DNS 查询使用系统解析器；内网名称无法解析时，可以先使用已知 IP 或默认 VPN 模式连接后解析。

系统代理及其他 VPN 可能改变应用的实际出口。“出口诊断”报告系统 IP 路由选择，不能单独证明浏览器请求的实际路径。关闭客户端窗口不会自动断开 VPN。

## 隐私

本工具通过 Windows 系统窗口认证，不接收或记录账号密码，没有遥测或日志上传功能。草稿和本工具连接的归属标记保存在本机 `%LOCALAPPDATA%\SJTU-Link`。Windows 自身的凭据处理由系统负责。

仓库不包含用户账号、密码、电话簿、个人规则、连接日志或用户截图。学校服务器域名及公开帮助链接是产品的必要配置，不是个人账号信息。详见 [PRIVACY.md](PRIVACY.md)。

## 构建与测试

在源码目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-ui.ps1
```

构建使用 Windows 自带的 .NET Framework C# 编译器，无需下载第三方库。执行策略参数只影响该次进程，不修改系统全局策略。

`tests.ps1` 使用内存模拟验证规则校验、归属保护和故障恢复，不修改真实 VPN 配置。`test-ui.ps1` 检查界面布局、规则预览及数据往返，不进行登录或真实 VPN 配置修改。真实连接依赖有效的学校账号和网络环境。

## 移除

先断开连接，在“使用说明”页删除本工具的 VPN 配置，再删除解压文件夹。此操作保留本机规则草稿，不会卸载 aTrust。

## 参考与许可

- [学校 Windows VPN 使用说明](https://net.sjtu.edu.cn/info/1200/3286.htm)
- [学校 VPN 服务页面](https://net.sjtu.edu.cn/xxfw/VPN.htm)

采用 [MIT License](LICENSE)。
