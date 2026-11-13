## How to upgrade bundled Tor version in Wasabi Wallet

### Automatically

Invoke the following commands in your shell:

```sh
cd Contrib/BundledApps

# See what the latest released Tor Browser version is here: https://www.torproject.org/download/
# Suppose it is "11.5.2". Then one can just call:
./upgrade-tor.sh -version "11.5.2"
```

### Manually

1. Check if there is a new Tor Browser version.
    * The Tor Browser changelog can be found here: https://gitweb.torproject.org/builders/tor-browser-build.git/plain/projects/browser/Bundle-Data/Docs/ChangeLog.txt
    * The Tor changelog can be found here: https://gitweb.torproject.org/tor.git/plain/ChangeLog
2. [Download](https://www.torproject.org/download/) the latest stable Tor Browser for Linux (x64 and arm64).

#### Linux

1. Open `tor-browser-linux64-<version>_en-US.tar.xz` file in a zip decompressing tool (like 7zip).
1. Navigate to `tor-browser_en-US\Browser\TorBrowser\Tor` folder in the archive.
1. Unzip it to `WalletWasabi\BundledApps\Binaries\linux-x64\Tor` folder.
    * Do not copy the `PluggableTransports` folder.

#### Upgrade Geoip files

1. Open `tor-browser-linux64-<version>_en-US.tar.xz` file in a zip decompressing tool (like 7zip).
1. Navigate to `tor-browser_en-US/Browser/TorBrowser/Data/Tor` folder in the archive.
1. Unzip it to `WalletWasabi/WalletWasabi/Tor/Geoip` folder.
