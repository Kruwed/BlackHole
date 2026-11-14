## Release Highlights

#### ❗ Security updates
#### 🔧 Improved coinjoin stability
#### 🌙 Payjoin discontinued

## Release Summary
Wasabi Wallet v2.8.5 includes security updates and polishes the coinjoin feature.

### ❗ Security updates
Updates dependencies to address disclosed vulnerabilities & adds stricter transaction verification to protect against malicious servers.

### 🔧 Improved coinjoin stability
Fixes an edge case where clients would fail to construct outputs. Inputs from partially signed coinjoins containing payments are now prioritized for the next round. Amounts below 10,000 sats can no longer be queued for coinjoin payments and get stuck.

### 🌙 Payjoin discontinued
Support for sending BIP78 payjoin transactions was removed to reduce code complexity and attack surface.
