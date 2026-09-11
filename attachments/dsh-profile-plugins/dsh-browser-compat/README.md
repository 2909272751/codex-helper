# dsh-browser-compat

外网明文 HTTP（例如 88frp 隧道）访问 DSH Web 时，浏览器把该地址视为非安全上下文，
`crypto.randomUUID` 不可用，而 DSH 前端无条件调用它，导致连接循环报错、页面空白。

本插件在 host 侧通过 `webServer.tapIndex` 在 `index.html` 的 `<head>` 最前插入一段
classic script：仅在 `crypto.randomUUID` 缺失时，用 `crypto.getRandomValues` 按
UUIDv4（RFC 4122）生成。不新增任何授权、host 白名单或安全校验绕过；
`getRandomValues` 本就是浏览器在非安全上下文仍提供的熵源。

- 依赖：仅 DSH 自带的 `webServer` 服务，不联网、不读会话/凭据/设置。
- 生效：Host 侧 index 渲染时注入，禁用插件即移除（`ctx.effect` 作用域）。
