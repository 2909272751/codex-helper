// DSH Web catalogue-first bootstrap.
//
// This is deliberately a request-ordering bridge, not a cache and not a
// session reader: it never inspects, retains, transmits, or writes response
// bodies.  A small catalogue preflight lets the sidebar become usable before
// the upstream app asks for the optional recent-conversation history window.
if (typeof window !== "undefined" && window.__ModuleLoader__) {
  window.__ModuleLoader__.load({
    id: "dsh-catalog-fastpath",
    factory: () => {
      const module = { exports: {} };
      const exports = module.exports;

      const WRAPPED_FETCH = Symbol.for("dsh-catalog-fastpath.wrapped-fetch");
      const READY_PROMISE = Symbol.for("dsh-catalog-fastpath.catalogue-ready");
      const PREFLIGHT_STARTED = Symbol.for("dsh-catalog-fastpath.preflight-started");
      const GATE_TIMEOUT_MS = 2000;

      function bodyText(input) {
        if (!input || typeof input !== "object") return "";
        if (typeof input.body === "string") return input.body;
        return "";
      }

      function requestMethod(input, init) {
        const url = typeof input === "string" ? input : input && input.url;
        const text = bodyText(init) || bodyText(input);
        let method = "";
        try {
          const parsed = text ? JSON.parse(text) : null;
          method = String(parsed && parsed.method || "").toLowerCase();
        } catch {
          // Non-JSON fetches are unrelated to DSH RPC.
        }
        if (!method && typeof url === "string") {
          const match = url.match(/\/api\/(session[./][^/?#]+)/i);
          method = match ? match[1].toLowerCase() : "";
        }
        return method;
      }

      function isHistoryRequest(input, init) {
        const method = requestMethod(input, init);
        return method === "session.history"
          || method === "session/history"
          || method === "session.page"
          || method === "session/page";
      }

      function startCataloguePreflight(originalFetch) {
        if (window[PREFLIGHT_STARTED]) return window[READY_PROMISE];
        window[PREFLIGHT_STARTED] = true;

        const ready = Promise.race([
          originalFetch("/api/session/list", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({
              type: "client-request",
              rpcId: "catalogue-fastpath-" + Date.now().toString(36),
              method: "session/list",
              payload: { args: { _request: {} } }
            })
          }).then(() => undefined, () => undefined),
          new Promise(resolve => window.setTimeout(resolve, GATE_TIMEOUT_MS))
        ]);
        window[READY_PROMISE] = ready;
        return ready;
      }

      function install() {
        if (window.fetch[WRAPPED_FETCH]) return;
        const originalFetch = window.fetch.bind(window);
        const catalogueReady = startCataloguePreflight(originalFetch);

        const bridgeFetch = async (input, init) => {
          if (isHistoryRequest(input, init)) await catalogueReady;
          return originalFetch(input, init);
        };
        bridgeFetch[WRAPPED_FETCH] = true;
        window.fetch = bridgeFetch;
      }

      function apply() {
        install();
      }

      exports.apply = apply;
      return module.exports;
    }
  });
}
