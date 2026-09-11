/**
 * dsh-browser-compat — insecure-origin Web client compatibility.
 *
 * Why: browsers restrict `crypto.randomUUID` to secure contexts (HTTPS /
 * localhost). Accessing the Web GUI through a plain-HTTP remote address
 * (for example an 88frp tunnel to 127.0.0.1:3080) is not a secure context, so
 * `crypto.randomUUID` is simply absent while the DSH client calls it
 * unconditionally — the connection loop throws and the UI stays empty.
 *
 * What: this host plugin registers a `webServer.tapIndex` transform that
 * inserts one classic script at the front of `index.html` <head>. The script
 * defines `crypto.randomUUID` only when it is missing, using the UUIDv4
 * algorithm over `crypto.getRandomValues` — an entropy source that stays
 * available on insecure origins.
 *
 * Boundary: this is a compatibility shim, not an authorization change. It
 * adds no credentials, no origin/host allowance, and no bypass of DSH's
 * trusted-host or permission checks. `crypto.getRandomValues` output is
 * already the runtime's entropy source; the shim only re-shapes 16 random
 * bytes into the RFC 4122 version-4 textual form.
 *
 * The host half reads no settings, sessions, credentials or files.
 */

/** Cordis plugin name. */
const name = "dsh-browser-compat";

/** The index transform needs the HTTP route registry. */
const inject = ["webServer"];

/**
 * UUIDv4 polyfill installed into the page head. Plain ES5-compatible
 * JavaScript on purpose: it must run before any module script on browsers old
 * enough to lack `crypto.randomUUID`.
 */
const RANDOM_UUID_SHIM = [
  "(function () {",
  "  try {",
  "    var c = window.crypto;",
  "    if (!c) return;",
  "    if (typeof c.randomUUID === 'function') return;",
  "    if (typeof c.getRandomValues !== 'function') return;",
  "    var HEX = [];",
  "    for (var i = 0; i < 256; i++) HEX[i] = (i + 0x100).toString(16).slice(1);",
  "    function uuidV4() {",
  "      var b = new Uint8Array(16);",
  "      c.getRandomValues(b);",
  "      b[6] = (b[6] & 0x0f) | 0x40;",
  "      b[8] = (b[8] & 0x3f) | 0x80;",
  "      return HEX[b[0]] + HEX[b[1]] + HEX[b[2]] + HEX[b[3]] + '-'",
  "        + HEX[b[4]] + HEX[b[5]] + '-' + HEX[b[6]] + HEX[b[7]] + '-'",
  "        + HEX[b[8]] + HEX[b[9]] + '-'",
  "        + HEX[b[10]] + HEX[b[11]] + HEX[b[12]] + HEX[b[13]] + HEX[b[14]] + HEX[b[15]];",
  "    }",
  "    try {",
  "      Object.defineProperty(c, 'randomUUID', { value: uuidV4, writable: true, configurable: true });",
  "    } catch (e) {",
  "      c.randomUUID = uuidV4;",
  "    }",
  "  } catch (e) { /* never break index rendering */ }",
  "})();"
].join("\n");

/** Classic script tag carrying {@link RANDOM_UUID_SHIM}. */
const SHIM_TAG = "<script>" + RANDOM_UUID_SHIM + "</" + "script>";

/**
 * Insert the shim as the first thing inside `<head>` so it executes before
 * every other startup script. Documents without a `<head>` tag fall back to
 * prepending the tag to the body, which still executes first.
 * @param html - the raw index.html body.
 * @returns the transformed body.
 */
function injectShim(html) {
  if (typeof html !== "string" || html.length === 0) return html;
  if (html.indexOf("randomUUID") !== -1) return html;
  const head = html.match(/<head[^>]*>/i);
  if (head && typeof head.index === "number") {
    const at = head.index + head[0].length;
    return html.slice(0, at) + SHIM_TAG + html.slice(at);
  }
  return SHIM_TAG + html;
}

/**
 * Mount the index tap. The returned disposer is fiber-scoped through
 * `ctx.effect`, so disabling or reloading the plugin removes the transform.
 * @param ctx - host plugin context carrying webServer.
 */
function apply(ctx) {
  ctx.effect(() => ctx.webServer.tapIndex(injectShim));
}

export { apply, inject, injectShim, name };
