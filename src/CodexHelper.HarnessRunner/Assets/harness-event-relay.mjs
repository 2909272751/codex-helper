// 仅供 Codex Helper Harness Runner 使用的本地事件中继。
// 它与 DSH 同样运行在 Node 内，只把 mux 原始帧经 stdout 管道交回 Runner；
// 不接收合同正文、密钥或会话 ID，也不把事件正文写入文件或控制台日志。
const baseUrl = process.env.CODEX_HELPER_DSH_BASE_URL;
const sessionId = process.env.CODEX_HELPER_DSH_SESSION_ID;
if (!baseUrl) {
  process.stderr.write('missing DSH base URL\n');
  process.exit(64);
}

const base = new URL(baseUrl);
const wsScheme = base.protocol === 'https:' ? 'wss:' : 'ws:';
const muxUrl = `${wsScheme}//${base.host}/api/events.mux`;
const hostUrl = `${wsScheme}//${base.host}/api/events.host`;
let shuttingDown = false;
let opened = 0;
let ready = false;
const pending = [];

function fail(message) {
  if (shuttingDown) return;
  shuttingDown = true;
  process.stderr.write(`${message}\n`);
  process.exitCode = 1;
  setTimeout(() => process.exit(1), 10).unref();
}

function openedOne() {
  opened += 1;
  if (opened === 2) {
    ready = true;
    process.stdout.write('{"type":"helper/relay-ready"}\n');
    for (const frame of pending.splice(0)) process.stdout.write(frame + '\n');
  }
}

const mux = new WebSocket(muxUrl);
const host = new WebSocket(hostUrl);
mux.addEventListener('open', openedOne);
host.addEventListener('open', openedOne);
mux.addEventListener('message', event => {
  // Global mux 会重放所有历史会话；只转发当前合同会话，避免大量无关帧堵塞管道。
  // stdout 是由父进程读取的内存管道，绝不写入磁盘。保持匹配帧原形以兼容 DSH 新增字段。
  const frame = String(event.data);
  try {
    const parsed = JSON.parse(frame);
    if (sessionId && parsed?.payload?.sessionId !== sessionId) return;
  } catch { return; }
  if (!ready) pending.push(frame);
  else process.stdout.write(frame + '\n');
});
mux.addEventListener('close', () => fail('mux closed'));
host.addEventListener('close', () => fail('host closed'));
mux.addEventListener('error', () => fail('mux error'));
host.addEventListener('error', () => fail('host error'));

// 保持 stdin 打开，以便父进程结束时可以可靠终止该中继。
process.stdin.resume();
