/** Stable host-side identity for the profile bundle. */
const name = "dsh-model-vision-bridge";

/**
 * The bridge is deliberately client-only. DSH's LLM runtime remains the sole
 * source of model capability; this host half never reads settings, credentials
 * or sessions and never changes request dispatch.
 */
function apply() {}

export { apply, name };
