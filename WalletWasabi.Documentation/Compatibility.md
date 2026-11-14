# Abstract

This document lists the software that Black Hole is meant to run on. This does not necessarily mean that systems that are not listed will not work - they might work, but nobody looks after them.

# Supported Operating Systems

- Ubuntu 22.04+
- Fedora 42+
- Debian 12+

Windows and macOS are not supported.

# Supported Architectures

- x64
- arm64 (experimental)

# FAQ

## What is required to run Black Hole on the supported Operating Systems?

- A Bitcoin node whose RPC interface Black Hole can reach. It is the only source of blocks, unconfirmed transactions and fee estimations.
- .NET 10.0 [reqs](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
- Avalonia [reqs](https://github.com/AvaloniaUI/Avalonia/wiki/Runtime-Requirements).

## What about Tails and Whonix?

Tails and Whonix are privacy-oriented OSs, so it makes sense to use them with Black Hole. The dependencies do not officially support them, so there are no promises regarding future stability.

Black Hole automatically detects if it is running on Tails or Whonix and will use the already running system-wide Tor instance, instead of starting its own.
