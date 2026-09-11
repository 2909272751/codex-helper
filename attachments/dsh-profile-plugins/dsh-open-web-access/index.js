/**
 * dsh-open-web-access
 *
 * DSH 0.1.5 protects both the index document and /api with a per-process
 * launch token followed by a browser cookie.  That blocks trusted local
 * automation such as Codex Helper's health probe before it can submit a job.
 *
 * This plugin deliberately removes only that authentication layer.  It keeps
 * the connection service's existing loopback / configured trusted-host and
 * Origin checks.  It is therefore suitable only for a deliberately trusted
 * network entry point; it must not be enabled on an Internet-facing service
 * unless that network is independently access-controlled.
 */

const name = "dsh-open-web-access";
const inject = ["connection"];

function apply(ctx) {
  const connection = ctx.connection;
  const originalRequestRejection = connection.requestRejection.bind(connection);

  // Preserve the host/origin fence (403) and remove only browser auth (401).
  connection.requestRejection = (request) => {
    const rejection = originalRequestRejection(request);
    return rejection === 403 ? 403 : undefined;
  };

  // The static frontend resolves this method at request time, so the override
  // applies without replacing DSH's webserver or static-file implementation.
  connection.authorizeIndex = () => true;
  connection.authenticatedUrl = (baseUrl) => baseUrl;
}

export { apply, inject, name };
