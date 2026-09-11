# dsh-open-web-access

DSH 0.1.5+ uses a fresh launch token and browser cookie for every Web startup.
This Cordis plugin removes that authentication layer so a trusted local
automation client can probe and submit work without possessing a URL token.

It **does not** remove DSH's loopback/trusted-host and Origin checks.  Use it
only where the network route itself is trusted.  Disable the plugin entry in
`cordis.patch.yml` and restart DSH to restore stock token protection.
