# Yizuka Cloud

个人云盘的 Windows Cloud Files API 客户端和网页界面源码。Windows 客户端在资源管理器中提供按需云文件夹、缩略图、托盘菜单、局域网优先连接与公网分块上传。后端使用 Copyparty；服务器的数据目录和账户配置不在本仓库。

## 安装

从 [Releases](https://github.com/Yizuka17/Yizuka_Cloud/releases) 下载 `YizukaCloudSetup.exe`，校验 SHA-256，然后在 Windows 10/11 x64 上运行。选择本机 NTFS/ReFS 文件夹作为云文件夹和缓存目录，输入云盘账号密码。安装包使用项目自签名证书，安装时会将公钥证书加入当前用户的受信任人证书库；它没有公共 CA 签名。首次安装需要能访问 `cloud.17yizuka.com`。详细说明见 [正式版使用说明](outputs/小云盘/正式版使用说明.md)。

客户端启动后优先尝试本机 `127.0.0.1:3924`、局域网 `17yizuka:8443`，最后使用 Cloudflare 公网地址。网页入口为 [cloud.17yizuka.com](https://cloud.17yizuka.com/)；其他电脑所在网络如果不能解析局域网主机名，会自动走公网。

## 源码

- `work/yizuka-cfapi`：Cloud Files API 客户端与同步逻辑。
- `work/yizuka-thumbnail-handler`：资源管理器缩略图 COM 服务。
- `work/yizuka-cloudfiles-setup`：自包含 Windows 安装程序。
- `work/yizuka-msix/release-staging`：MSIX 清单与图标。
- `outputs/小云盘/ui` 与 `ui-server.mjs`：网页界面与代理层。发布的 `ui-server.mjs` 从 `YIZUKA_SECURE_HOSTS` 环境变量读取允许的 HTTPS Host 列表；仓库不含服务器的运行配置。

客户端可用 .NET 8 SDK 执行 `dotnet build work/yizuka-cfapi/yizuka-cfapi.csproj -c Release`。完整 MSIX/安装包还需要 Windows SDK 的 `makeappx.exe`、`signtool.exe`，以及你自己的代码签名证书。设置 `YIZUKA_SIGNING_THUMBPRINT`，必要时设置 `MAKEAPPX_EXE` 和 `SIGNTOOL_EXE`，再运行 `work/yizuka-msix/build-release.ps1`。构建脚本会生成 `outputs/小云盘/YizukaCloudSetup.exe` 与 SHA-256 文件。

## 数据与限制

账号密码保存在每台电脑当前用户的 DPAPI 文件中，不能跨用户复制。云端数据不随卸载客户端删除。客户端离线时保留占位文件；未缓存的内容要联网才能打开。冲突检测依赖服务端的 WebDAV 大小和修改时间；同一秒内的同大小并发修改可能无法识别。Windows 可以回收干净文件的本地缓存，但当前版本没有精确的缓存容量上限。首次登录自启动由 MSIX 声明，本机尚未做重启后的人工验证。

仓库刻意不包含用户文件、账户配置、密码、密钥、日志、证书私钥和服务端运行目录。不要把这些内容提交到 Git。
