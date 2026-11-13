# Abstract

This document lists all the officially supported software by Wasabi Wallet. This means that Wasabi is tested on those systems, and we put all the efforts to make it work and maintain compatibility. One of our main goals is to not break the user-space, so we have to set up boundaries that we can responsibly maintain. This does not necessarily mean that systems that are not listed will not work - they might work, but we do not officially support them. There are a lot of potentially supported systems out there and more to come, but we can only promise support and stability on platforms that our dependencies support, too.

# Officially Supported Operating Systems

- Windows 10 1607+
- Windows 11 23H2+
- macOS 14+
- Ubuntu 22.04+
- Fedora 42+
- Debian 12+

# Officially Supported Architectures

- x64 (Windows, Linux, macOS)
- arm64 (macOS, Linux (experimental))

# FAQ

## What is required to run Wasabi Wallet on officially supported Operating Systems?

Wasabi dependencies are:
- .NET 10.0 [reqs](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).
- Avalonia [reqs](https://github.com/AvaloniaUI/Avalonia/wiki/Runtime-Requirements).

## What about Tails and Whonix?

Tails and Whonix are privacy-oriented OSs, so it makes sense to use them with Wasabi Wallet. At the moment, Wasabi is working properly on these platforms, but our dependencies do not officially support them, so we cannot make promises regarding future stability.

Wasabi automatically detects if it is running on Tails or Whonix and will use the already running system-wide Tor instance, instead of starting its own.

On Wasabi version [2.7.2](https://github.com/WalletWasabi/WalletWasabi/releases/tag/v2.7.2) and earlier, Wasabi should be started with the following startup parameter: `--UseTor=EnabledOnlyRunning`.
