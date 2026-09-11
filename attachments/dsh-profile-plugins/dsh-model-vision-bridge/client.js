// DSH Web client bridge: normalize the two public capability field spellings
// used by DSH client/runtime versions. It never adds a modality; an `image`
// value can only reach the UI when the host runtime already returned it.
if (typeof window !== "undefined" && window.__ModuleLoader__) {
  window.__ModuleLoader__.load({
    id: "dsh-model-vision-bridge",
    factory: () => {
      const module = { exports: {} };
      const exports = module.exports;

      const ORIGINAL_FETCH = Symbol.for("dsh-model-vision-bridge.original-fetch");
      const WRAPPED_FETCH = Symbol.for("dsh-model-vision-bridge.wrapped-fetch");

      function normalizeModelCapability(value, seen) {
        if (value === null || typeof value !== "object") return false;
        if (seen.has(value)) return false;
        seen.add(value);

        let changed = false;
        if (!Array.isArray(value.input) && Array.isArray(value.inputModalities)) {
          value.input = value.inputModalities.slice();
          changed = true;
        }
        if (!Array.isArray(value.inputModalities) && Array.isArray(value.input)) {
          value.inputModalities = value.input.slice();
          changed = true;
        }
        for (const child of Object.values(value)) {
          if (Array.isArray(child)) {
            for (const entry of child) changed = normalizeModelCapability(entry, seen) || changed;
          } else {
            changed = normalizeModelCapability(child, seen) || changed;
          }
        }
        return changed;
      }

      function isJsonApiResponse(response) {
        const type = response.headers.get("content-type") || "";
        return response.ok && type.toLowerCase().includes("application/json") && response.url.includes("/api/");
      }

      function install() {
        if (window.fetch[WRAPPED_FETCH]) return;
        const originalFetch = window.fetch.bind(window);
        originalFetch[ORIGINAL_FETCH] = true;

        const bridgeFetch = async (...args) => {
          const response = await originalFetch(...args);
          if (!isJsonApiResponse(response)) return response;
          try {
            const payload = await response.clone().json();
            if (!normalizeModelCapability(payload, new WeakSet())) return response;
            const headers = new Headers(response.headers);
            headers.set("content-type", "application/json; charset=utf-8");
            return new Response(JSON.stringify(payload), {
              status: response.status,
              statusText: response.statusText,
              headers
            });
          } catch {
            // Preserve the original RPC response when it is not ordinary JSON.
            return response;
          }
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
