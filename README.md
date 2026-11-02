# PengGAP

> **PengGAP v0.01.001 来自 FastGitHub-2.1.5**

本仓库是 [FastGithub](https://github.com/dotnetcore/FastGithub) **2.1.5** 的衍生版本，已更名为 **PengGAP**。
按 MIT 协议保留原始版权声明（Copyright © 2021 老九 / dotnetcore/FastGithub），并在此声明所做的修改。
许可全文见 [LICENSE](LICENSE)。

PengGAP 是 GitHub 访问加速工具，用于解决 github 打不开、头像无法加载、releases 上传下载失败、
`git clone` / `git pull` / `git push` 失败等问题。

> **它不是"翻墙"工具**，没有相关计划，也不为游戏加速。
> 它只做三件事：解析出**未被污染的 IP**、从中**挑选最快的**、然后用本地证书中转 TLS。

---

## 1. 命名对照

本次更名涉及全部代码标识符与可执行文件，与上游的对应关系如下：

| 项 | 上游 FastGithub | 本仓库 PengGAP |
|---|---|---|
| 产品名 | FastGithub | **PengGAP** |
| 解决方案 | `FastGithub.sln` | `PengGAP.sln` |
| 项目 / 命名空间 | `FastGithub.*` | `PengGAP.*` |
| 核心类型 | `FastGithubConfig`、`FastGithubOptions`… | `PengGAPConfig`、`PengGAPOptions`… |
| 主程序 | `fastgithub.exe` / `fastgithub` | `penggap.exe` / `penggap` |
| 界面程序 | `FastGithub.UI.exe` | `PengGAP.UI.exe` |
| 配置节 | `"FastGithub"` | `"PengGAP"` |
| 自签证书 | `cacert/fastgithub.cer`、`fastgithub.key` | `cacert/penggap.cer`、`penggap.key` |
| 单实例互斥体 | `Global\FastGithub` | `Global\PengGAP` |
| Windows 服务名 | `FastGithub.dnscrypt-proxy` | `PengGAP.dnscrypt-proxy` |
| 发布目录 | `fastgithub_win-x64` | `penggap_win-x64` |

---

## 2. 相对上游的修改

| 类别 | 修改内容 |
|---|---|
| 更名 | 全仓库 358 处标识符、8 个项目、解决方案、可执行文件、服务名、证书名、配置节全部更名 |
| 框架 | `net7.0`（已 EOL）→ **`net8.0` LTS**；UI 仍为 `net48` |
| 依赖 | Yarp `1.1.1 → 2.2.0`（修复 GHSA-jrjw-qgr2-wfcg 高危漏洞）、`Microsoft.Extensions.*` 升级到 8.0.x、Serilog 全套升级、新增 `System.Text.Json 8.0.6` 覆盖 |
| 漏洞 | 依赖漏洞从 **5 个高危降到 0 个** |
| 构建 | 移除全局 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`（原先会污染 net48 的 UI 项目），发布需显式 `-r`；依赖版本全部固定，构建可复现；UI 项目加 `Microsoft.NETFramework.ReferenceAssemblies`，无需安装 Visual Studio 即可编译 |
| 正确性 | `PengGAPConfig` 改为**不可变快照 + 原子替换**，并给匹配缓存加上限（原先会被随机子域名撑爆内存）；`CertService` 的 CA 懒加载改为线程安全；`DnsClient` 的信号量等待改为传递取消令牌；`HttpClientHandler` 的 Host 头不再丢失非默认端口 |
| 性能 | 节点测速由**串行改为并发**（上限 8）；测速循环改为补偿式间隔，不再重叠堆积；`MemoryCache` 实现 `IDisposable`；PAC 脚本按 Host 缓存；DNS 拦截增加零分配报文头预判 |
| 配置 | `TomlUtil` 改为**逐行手术式改写** dnscrypt-proxy 配置（原先会抹掉 857 行注释、且非原子写）；删除会真实劫持域名并返回 404 的示例规则；`FallbackDns` 调整为国内优先 |
| 安全 | 根 CA 改为**短期轮换**（7 天，剩余不足 1 天自动重建）；私钥在 Windows 上用 **DPAPI 加密**存储、Unix 为 `0600`；收紧权限失败**即中止启动**；信任装到**当前用户**而非机器级存储；叶证书缩短为 24 小时；只对配置规则内域名签发，规则外返回自签名证书 |
| 证书 | 根 CA 7 天轮换 + 运行期守护（`CertGuardHostedService`）；叶证书缓存键绑定 CA 指纹，轮换后旧证书自然失效；安装后回读证书存储校验 |
| 审计修复 | 隧道半关闭截断（`WhenAny` → `WhenAll`）；证书链校验组合标志绕过；`ownsSocket:false` 句柄泄漏；UDP 日志接收挂起；TLS/连接失败未释放句柄；dnscrypt-proxy 静默失败；hosts 别名误伤；`ProxyOverride` 写入通配符 |
| 界面 | 流量图横纵坐标固定与整数刻度；新增「证书」页；删除「问答」页；托盘证书状态提醒；不可见时停止轮询 |
| 观察 | 启动配置自检；转发失败记录 host/path/error；证书状态回读校验 |

---

## 3. 功能

- 域名的纯净 IP 解析（内置 `dnscrypt-proxy`，DNSCrypt / DoH）
- IP 测速并自动选择最快的 3 个
- 按域名自定义 TLS SNI 与证书校验策略
- Google / bootcss / Azure 等 CDN 资源替换
- Windows 下通过 WinDivert 做**透明拦截**，用户无需手动设置代理

---

## 4. 运行要求

- Windows 10+ / Windows Server、Linux x64/arm64、macOS x64/arm64
- **必须管理员 / root 权限**：安装 WinDivert 驱动、修改 hosts、写注册表与 `cacert` 目录
- 首次运行需要能访问 `raw.githubusercontent.com` 或 `download.dnscrypt.info`，用于拉取 dnscrypt-proxy 的服务器列表

---

## 5. 部署

### 5.1 Windows 桌面

双击 `PengGAP.UI.exe`。它会自动拉起 `penggap.exe` 并常驻托盘；
界面提供 **流量 / 日志 / 证书** 三个页面，关闭界面即退出后端。

### 5.2 Windows 服务

```
penggap.exe start    以 Windows 服务安装并启动
penggap.exe stop     停止并卸载服务
```

### 5.3 Linux

```
sudo ./penggap                前台运行
sudo ./penggap start          安装为 systemd 服务并启动
sudo ./penggap stop           停止并卸载 systemd 服务
```

然后把系统代理设为 `http://127.0.0.1:38457`，或手动代理 HTTP/HTTPS 到 `127.0.0.1:38457`。

### 5.4 macOS

1. 首次运行若提示"无法验证开发者"，在程序目录执行：
   `sudo xattr -d com.apple.quarantine *.*`
2. 运行后目录下会生成 `cacert/penggap.cer`，双击导入"钥匙串访问"，
   找到 `PengGAP` 证书 → 展开"信任" → 设为**始终信任**。
3. 系统设置 → 网络 → 高级 → 代理，勾选"自动代理配置"填入程式提示的地址，
   或手动填 HTTP/HTTPS 代理 `127.0.0.1:38457`。

### 5.5 端口

| 端口 | 用途 | 平台 |
|---|---|---|
| `38457` | HTTP 正向代理 | 仅 Linux / macOS |
| `80`、`443` | HTTP(S) 反向代理 | 仅 Windows |
| `22` | GitHub SSH 反向代理 | 仅 Windows |
| `9418` | `git://` 协议反向代理 | 仅 Windows |
| `5533` 起 | dnscrypt-proxy 本地监听 | 全平台 |

> Windows 上若 80/443/22/9418 已被占用，会自动向上寻找可用端口，
> 再由 WinDivert 把原端口的流量重写过去，因此实际端口可能变化。

### 5.6 从源码构建

```
publish.cmd
```

产物在 `publish/penggap_win-x64/`、`publish/penggap_linux-x64/` 等目录。

---

## 6. 配置

主配置 `appsettings.json`，以及会被自动加载的 `appsettings/appsettings.*.json`。
**修改后需要重启程序**。

```jsonc
{
  "PengGAP": {
    "HttpProxyPort": 38457,          // 仅 Linux/macOS 使用的 HTTP 代理端口
    "FallbackDns": [ "223.5.5.5:53", "114.114.114.114:53", "8.8.8.8:53" ],
    "DomainConfigs": {
      "*.example.com": {
        "TlsSni": false,                 // 是否在 TLS 握手时发送 SNI，默认 false
        "TlsSniPattern": null,           // @domain / @ipaddress / @random
        "TlsIgnoreNameMismatch": true,   // 忽略服务器证书域名不匹配
        "Timeout": null,                 // 如 "00:02:00"
        "IPAddress": null,               // 强制连接指定 IP
        "PreferIPv4": false,             // 优先使用 IPv4（部分 CDN 的 IPv6 建连快但传输慢）
        "Destination": null,             // 改写目的地，绝对或相对 Uri
        "Response": null                 // 设置后直接阻断并响应，其它字段失效
      }
    }
  }
}
```

域名表达式中的 `*` 匹配**除 `.` 之外**的 0~n 个字符。
匹配优先级为**段数少的先生效**；同段数时自右向左比较，字面量优先于通配符。

---

## 7. 证书

### 7.1 工作机制

程序用一把**本地自签根 CA** 为被加速的域名现场签发叶证书，从而中转 TLS：

| 证书 | 有效期 | 说明 |
|---|---|---|
| 根 CA | **7 天** | 启动时检查；运行中每 30 分钟检查，剩余不足 1 天时自动轮换 |
| 叶证书 | **24 小时** | 域名首次握手时现场生成，私钥只在内存、不落盘 |

- 只对**配置规则内**的域名签发根 CA 签名的证书；规则外域名返回自签名证书，避免本机被当作任意域名的签发服务
- 根证书安装到**当前用户**的受信任根，不影响同机其他用户
- 每次安装后会**回读证书存储校验**，失败会记 Error 并在托盘弹出提示

### 7.2 文件

- `penggap.cer`（Linux 为 `.crt`）—— 需要安装并信任
- `penggap.key` —— **CA 私钥，切勿外泄**

### 7.3 私钥保护等级

| 平台 | 存储方式 | 能挡住 | 挡不住 |
|---|---|---|---|
| Windows | **DPAPI 加密**（当前用户作用域），文件头 `PGAPDK1` | 文件被复制到其他机器后**无法解密**；其他用户无法读取 | 以**同一用户**身份运行的进程仍可解密 |
| Linux / macOS | 明文 PEM + 权限 `0600` | 同机其他用户无法读取 | 同用户进程、root，以及文件被复制后可读 |

共同措施：

- 权限收紧失败**中止启动**，不会静默降级
- 先建空文件、收紧权限、最后写入内容，消除"先写后收紧"的窗口
- 轮换时旧私钥**先随机覆写再删除**
- 根 CA 7 天轮换，泄漏窗口有上限

> 覆写删除在 SSD 上受**磨损均衡**影响，不能保证命中同一物理块，只能降低、不能消除残留风险。

### 7.4 安装信任

Windows 启动时自动装到当前用户受信任根。若被组策略或安全软件拦截：

1. 看日志是否出现 `根CA未被系统信任`
2. 手动双击 `cacert/penggap.cer` → 安装到"受信任的根证书颁发机构"

安装后程序会尝试为 Git 设置 `http.sslbackend schannel`；若 git 仍提示 `SSL certificate problem`：

```
git config --global http.sslbackend schannel
```

Firefox 使用独立证书库，需单独导入：设置 → 隐私与安全 → 证书 → 查看证书 → 证书颁发机构 → 导入。

### 7.5 查看已签发证书

界面「证书」页列出根证书与已签发的叶证书（名称 / 类型 / 签发日期 / 到期日期 / 状态），**过期的也会显示**：

- 根证书以 `cacert/` 下的文件为准，**删除文件后即从列表消失**
- 叶证书记录保存在内存中，**重启程序后清空**（叶证书本身不落盘，会按需重新签发）

---

## 8. 第三方组件

- [`dnscrypt-proxy`](https://github.com/DNSCrypt/dnscrypt-proxy) —— ISC 协议，
  许可证见 `@dnscrypt-proxy/LICENSE`，随程序一同分发时必须保留。

---

## 9. 变更记录

### 2026-11-02 ~ 11-03

**缺陷修复（影响可用性）**

- **裁剪导致配置全部失效**：此前以 `PublishTrimmed` 发布，`ConfigurationBinder` 的反射赋值被破坏，55 条域名规则静默退化为默认值（`TlsSni` / `TlsIgnoreNameMismatch` / `Destination` 全部失效）。已关闭裁剪，并新增启动**配置自检**日志用于早期发现退化。
- **证书无法被信任**（浏览器报 `ERR_CERT_AUTHORITY_INVALID`）：Windows 安装器原先是"先删除同主题旧根、再添加新根"，而现代 Windows 会拒绝对 `CurrentUser\Root` 的删除操作（`Access is denied`），异常被外层 `catch` 吞掉，导致**新根永远装不上**。已改为"先装新根、旧根清理放到独立 try 中"。
- **隧道半关闭截断**：`TunnelMiddleware` / `TcpReverseProxyHandler` 用 `Task.WhenAny` 后立即释放连接，客户端半关闭时上游响应被截断（`git clone` / `ssh` 报 `early EOF`）。改为 `Task.WhenAll`。
- **证书链校验被绕过**：证书错误同时包含"链错误 + 名称不匹配"时，`HasFlag(RemoteCertificateNameMismatch)` 仍命中，`TlsIgnoreNameMismatch` 会把链错误一并放行。改为仅当错误**恰好**为名称不匹配时才放宽。
- **socket / 句柄泄漏**：`TcpReverseProxyHandler` 的 `ownsSocket: false`；`HttpClientHandler` 在连接失败与 TLS 握手失败时未释放；UDP 日志接收回调抛异常导致等待方永久挂起。均已修复。
- **DNS 解析被兜底源拖慢**：每个冷域名都会串行等待不可用的兜底 DNS 超时（`119.29.29.29` 的 TCP/53 在本机必然失败），实测单个域名解析被拖到 6~14 秒、一轮测速扫描约 30 秒。改为 dnscrypt-proxy 返回结果后立即 `yield break`，兜底源仅在其不可用时使用；兜底地址改为 `223.5.5.5`。
- **dnscrypt-proxy 静默失败**：服务安装返回值被丢弃、进程退出只置空端点不记日志。已补充 Error 日志。
- **hosts 误伤用户配置**：多别名行只检查第二个字段，且还原时不加校验会把用户自己写的 `#penggap#` 行去前缀。已改为别名感知 + 编码保真 + 只还原真正的 hosts 行。
- **`ProxyOverride` 通配符**：Windows 该字段不支持 `*.example.com`，写入无效且残留脏数据。改为只写字面量主机名，并清理历史残留。

**证书架构（根 CA 生命周期收口）**

- 根 CA 有效期 10 年 → **7 天**，剩余不足 1 天自动轮换；启动时 + 运行中每 30 分钟检查
- 私钥 Windows 用 DPAPI 加密（文件头 `PGAPDK1`），兼容历史明文并自动迁移
- 证书目录与私钥文件权限收紧到仅当前用户，**失败即中止启动**；旧私钥随机覆写后删除
- 信任安装位置 `LocalMachine\Root` → **`CurrentUser\Root`**，并清理机器级残留
- 叶证书 1 年 → **24 小时**；缓存键加入 CA 指纹
- 只对配置规则内域名签发根 CA 签名证书，规则外返回自签名
- 安装后回读证书存储校验，状态变化才记日志

**新增能力**

- 界面新增**「证书」页**：列出根证书与已签发叶证书的名称、类型、签发/到期时间与状态（过期的也保留）
- 托盘**证书状态提醒**：根证书未被信任或即将到期时弹气泡（与界面是否隐藏无关）
- 新增 `/certificates` 端点；`/flowStatistics` 增加证书状态字段
- 配置项 **`PreferIPv4`**：对 `*.githubassets.com`、`*.githubusercontent.com` 开启，解决部分 CDN 的 IPv6 路径"建连快、传输慢"
- 流量图**横纵坐标固定**（X 为 60 秒滑动窗口、Y 为阶梯式上限、关闭动画），刻度一律取整

**工程**

- 删除「问答」页与遗留的 `WebBrowser` 初始化
- 清除全部编译警告，后端与 UI 均为 **0 错误 0 警告**

**性能实测结论**

对同一 GitHub CDN 资源做对照：网络基线（Cloudflare）**416 KB/s**，直连 Fastly **34.5 KB/s**，经加速器 **24.3 KB/s**，非配置的 Fastly 域名 **22 KB/s**。
说明当前慢的瓶颈在**上游 CDN 链路**，不在本程序；加速器的作用是"可用性"（纯净解析、IP 择优、SNI 规避、CDN 改写），而非提升上游带宽。

---

## 10. 许可与致谢

本项目以 [MIT](LICENSE) 协议发布。

- 原始项目：[dotnetcore/FastGithub](https://github.com/dotnetcore/FastGithub)，Copyright © 2021 老九
- 本衍生版：**PengGAP**，版本 **v0.01.001**，基于 **FastGitHub-2.1.5** 修改

感谢原作者的开源工作。
