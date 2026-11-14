# Guide for deterministic builds

The term *deterministic builds* is [defined](https://reproducible-builds.org/) as follows:

> Reproducible [or deterministic] builds are a set of software development practices that create an independently-verifiable path from source to binary code.

This guide describes how to reproduce the release packages of Black Hole. If you get stuck with these instructions, take a look at how to build Black Hole from source code in the [README](../../README.md).

## 1. How releases are produced

Every release artifact is produced by [`Contrib/release.sh`](../../Contrib/release.sh), run by the [release workflow](../../.github/workflows/release.yml) on GitHub-hosted runners when a `v*` tag is pushed:

| Job | Runner | Command | Artifacts |
| --- | --- | --- | --- |
| `debian-package-and-zips` | `ubuntu-latest` | `sudo bash -x ./Contrib/release.sh debian` | `BlackHole-<version>.deb`, `BlackHole-<version>-arm64.deb`, `BlackHole-<version>-linux-{x64,arm64}.{tar.gz,zip}` |
| `appimage-package` | `ubuntu-latest` | `sudo bash -x ./Contrib/release.sh appimage` | `BlackHole-<version>.AppImage`, `BlackHole-<version>-arm64.AppImage` |
| `sign-all-packages` | `ubuntu-latest` | `bash -x ./Contrib/release.sh gpgsign` | `SHA256SUMS.asc` (used in section 4) |

To reproduce a release, run **that release's own** `Contrib/release.sh`, checked out from the release tag.

The rest of this guide covers these Linux packages.

## 2. Assert correct environment

You need:

* a Linux system (the release workflow uses `ubuntu-latest`) with `git`, `zip`, `tar`, `dpkg-deb` (package `dpkg`), `file` and `curl`;
* the .NET SDK that ships the **same runtime pack** the release was built against (see below);
* root privileges, or one small adaptation (see below).

### Which .NET SDK?

`global.json` pins only the major version and uses `rollForward: latestFeature`, so the release workflow builds with whatever .NET SDK the runner image provides on release day. Do not guess it: the shipped package records the runtime it was built against. Read it from the artifact you want to reproduce:

```sh
# from the .deb
dpkg-deb --fsys-tarfile BlackHole-<version>.deb | tar -xO --wildcards '*WalletWasabi.Fluent.Desktop.runtimeconfig.json'
# or from the tar.gz
tar -xzO --wildcards -f BlackHole-<version>-linux-x64.tar.gz '*WalletWasabi.Fluent.Desktop.runtimeconfig.json'
```

The `"version"` field under `runtimeOptions.framework` is the runtime, for example `10.0.11`. Then install the **highest** SDK that ships that runtime, which is what `latestFeature` selects; the mapping is published in [dotnet/core `releases.json`](https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json).

If several SDKs are installed, pin the one you chose in `global.json` before building. Here an example:

```json
{
  "sdk": {
    "version": "10.0.400",
    "allowPrerelease": false,
    "rollForward": "disable"
  }
}
```

A convenient way to get exactly one SDK is the official container image, `mcr.microsoft.com/dotnet/sdk:<sdk-version>-noble`.

## 3. Reproduce the Linux packages

You can see the list of Black Hole releases here: https://github.com/Kruwed/BlackHole/releases. Each release has a git tag `v<version>`.

```sh
# Fetch exactly the tag (a shallow clone by tag name would silently prefer a same-named branch).
git init BlackHole && cd BlackHole
git remote add origin https://github.com/Kruwed/BlackHole.git
git fetch --depth 1 origin refs/tags/v<version>:refs/tags/v<version>
git checkout refs/tags/v<version>

# Same command the release workflow runs.
sudo bash -x ./Contrib/release.sh debian
```

The packages land in `packages/`:

```
packages/BlackHole-<version>.deb
packages/BlackHole-<version>-arm64.deb
packages/BlackHole-<version>-linux-x64.tar.gz
packages/BlackHole-<version>-linux-x64.zip
packages/BlackHole-<version>-linux-arm64.tar.gz
packages/BlackHole-<version>-linux-arm64.zip
```

The `debian` target builds both `linux-x64` and `linux-arm64` in one run.

## 4. Verify builds

Every release ships `SHA256SUMS.asc`, the list of package hashes clearsigned by the PGP key of the publisher of the release. Get that key from the publisher, verify the signature, then compare your packages against the signed list:

```sh
gpg --import <public key of the publisher>
gpg --decrypt SHA256SUMS.asc > SHA256SUMS.verified   # prints "Good signature from ..." and writes the signed list
cd packages && sha256sum -c --ignore-missing ../SHA256SUMS.verified
```

`sha256sum -c` prints `OK` for every package whose hash matches the published one. If a hash differs, extract both packages and compare them to find out why:

```sh
dpkg-deb -R BlackHole-<version>.deb official
dpkg-deb -R packages/BlackHole-<version>.deb built
diff -r official built
```

### What to expect

* **`.deb`: byte-identical.**
* **`.tar.gz`: byte-identical.** The tarball is created with `--sort=name`, `--mtime=@$SOURCE_DATE_EPOCH`, `--owner=0 --group=0 --numeric-owner` and the atime/ctime PAX headers removed.
* **`.zip`: differs**, because `zip -r` stores the files' modification times (see *Timestamps* above); it contains the same files that go into the `.tar.gz`.
