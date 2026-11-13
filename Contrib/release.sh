#!/usr/bin/env bash
#!nix-shell -i bash -p zip dpkg file curl

#------------------------------------------------------------------------------------#
#  release.sh                                                                        #
#                                                                                    #
#  This script builds the `WalletWasabi.Fluent.Desktop` for the supported Linux       #
#  platforms, creates the zips and tar.gz files for all of them, and creates the     #
#  .deb package and the AppImage.                                                    #
#------------------------------------------------------------------------------------#
set -xe

# Get the latest Git tag
LATEST_TAG=$(git describe --tags --abbrev=0)
# Extract the version number (strip the first character)
VERSION=${LATEST_TAG:1}
SHORT_VERSION=${VERSION:0:${#VERSION}-2}

# Update ClientVersion in Constants.cs with the version from git tag
CONSTANTS_FILE="./WalletWasabi/Helpers/Constants.cs"
VERSION_ARGS=$(echo "$VERSION" | sed 's/\./, /g')
sed -i "s/ClientVersion = new([0-9, ]*);/ClientVersion = new($VERSION_ARGS);/" "$CONSTANTS_FILE"

# Define project names
DESKTOP="WalletWasabi.Fluent.Desktop"
COORDINATOR="WalletWasabi.Coordinator"
DAEMON="WalletWasabi.Daemon"
DAEMON_PROJECT="./$DAEMON/$DAEMON.csproj"
DESKTOP_PROJECT="./$DESKTOP/$DESKTOP.csproj"
COORDINATOR_PROJECT="./$COORDINATOR/$COORDINATOR.csproj"

# Build directory
ROOT_DIR=$(pwd)
BUILD_DIR="$ROOT_DIR/build"

# Executable name
EXECUTABLE_NAME="wassabee"
COORDINATOR_EXECUTABLE_NAME="wcoordinator"

# Directory where to save the generated packages
PACKAGES_DIR="$ROOT_DIR/packages"

# Common name for all packages
PACKAGE_FILE_NAME_PREFIX="Wasabi-$VERSION"

ZIP="zip -r"
SOURCE_ROOT=$(pwd -P)

if [ "$1" = "debian" ]; then
  # Supported platforms
  PLATFORMS=("linux-x64" "linux-arm64")
  CREATE_DEBIAN_PACKAGE="yes"
  CREATE_APPIMAGE="no"
  RELEASE_NOTE="no"
  SIGN_PGP="no"
  PACKAGE_COORDINATOR="yes"
elif [ "$1" = "appimage" ]; then
  # Supported platforms
  PLATFORMS=("linux-x64" "linux-arm64")
  CREATE_DEBIAN_PACKAGE="no"
  CREATE_APPIMAGE="yes"
  RELEASE_NOTE="no"
  SIGN_PGP="no"
  PACKAGE_COORDINATOR="no"
elif [ "$1" = "releasenote" ]; then
  PLATFORMS=()
  CREATE_DEBIAN_PACKAGE="no"
  RELEASE_NOTE="yes"
  SIGN_PGP="no"
  PACKAGE_COORDINATOR="no"
elif [ "$1" = "gpgsign" ]; then
  PLATFORMS=()
  CREATE_DEBIAN_PACKAGE="no"
  RELEASE_NOTE="no"
  SIGN_PGP="yes"
  PACKAGE_COORDINATOR="no"
fi

# Remove the build directory if it exists and recreate it
# rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"

# Create packages directory (where all final packages are saved)
#rm -rf "$PACKAGES_DIR" &&
mkdir -p "$PACKAGES_DIR"

#------------------------------------------------------------------------------------#
# BUILD DESKTOP FOR ALL PLATFORMS                                                    #
#------------------------------------------------------------------------------------#
# Loop through each platform and build the project
for PLATFORM in "${PLATFORMS[@]}"; do
  # Define output directory for the platform
  OUTPUT_DIR=$BUILD_DIR/$PLATFORM

  if [[ "$PACKAGE_COORDINATOR" == "yes" ]]; then
    PROJECTS_TO_BUILD=("$DAEMON_PROJECT" "$DESKTOP_PROJECT" "$COORDINATOR_PROJECT" )
  else
    PROJECTS_TO_BUILD=("$DAEMON_PROJECT" "$DESKTOP_PROJECT" )
  fi

  for PROJECT in "${PROJECTS_TO_BUILD[@]}"; do
    # Build dotnet application
    dotnet restore $PROJECT --locked-mode
    dotnet publish $PROJECT \
            --configuration Release \
            --runtime $PLATFORM \
            --force \
            --output $OUTPUT_DIR \
            --self-contained true \
            --disable-parallel \
            --no-cache \
            --no-restore \
            --property:SelfContained=true \
            --property:VersionPrefix=$VERSION \
            --property:DebugType=none \
            --property:DebugSymbols=false \
            --property:ErrorReport=none \
            --property:DocumentationFile='' \
            --property:Deterministic=true \
            --property:PathMap="$SOURCE_ROOT=/src" \
            /clp:ErrorsOnly
  done

  # Rename executables as wassabee and wassabeed
  mv $OUTPUT_DIR/{$DESKTOP,${EXECUTABLE_NAME}}
  mv $OUTPUT_DIR/{$DAEMON,${EXECUTABLE_NAME}d}
  if [[ "$PACKAGE_COORDINATOR" == "yes" ]]; then
    mv $OUTPUT_DIR/{$COORDINATOR,${COORDINATOR_EXECUTABLE_NAME}}
  fi

  # Remove bundled app binaries for other platforms
  BUNDLED_APPS_DIR="$OUTPUT_DIR/BundledApps/Binaries"

  # The bundled binaries are stored in a folder with the same name as the platform (linux-x64 or linux-arm64).
  find $BUNDLED_APPS_DIR -mindepth 1 -maxdepth 1 -type d ! -name "$PLATFORM" -exec rm -rf {} +

  # Hack! *.deps.json files contains this SHA516 that depends on the absolute path of
  # the nuget packages. This means that these files are different in different computers
  # and for different users. (End goal: reproducibility)
  sed -i 's/"sha512": "sha512-[^"]*"/"sha512": ""/g' "$OUTPUT_DIR/$DESKTOP.deps.json"

  # Create compressed package files (.zip and .tar.gz)
  PACKAGE_FILE_NAME=$PACKAGE_FILE_NAME_PREFIX-$PLATFORM
  if [ -z "$SOURCE_DATE_EPOCH" ]; then
    export SOURCE_DATE_EPOCH=$(git log -1 --pretty=%ct)
  fi

  PARENT_DIR=$(dirname "$OUTPUT_DIR")
  BASE_NAME=$(basename "$OUTPUT_DIR")

  tar --sort=name \
      --mtime="@${SOURCE_DATE_EPOCH}" \
      --owner=0 \
      --group=0 \
      --numeric-owner \
      --transform="s|^$BASE_NAME|WasabiWallet|" \
      --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime \
      -pczf $PACKAGES_DIR/$PACKAGE_FILE_NAME.tar.gz \
      -C "$PARENT_DIR" \
      "$BASE_NAME"

  pushd "$OUTPUT_DIR" || exit
  $ZIP "$PACKAGES_DIR/$PACKAGE_FILE_NAME.zip" .
  popd || exit

done


#------------------------------------------------------------------------------------#
# CREATE DEBIAN PACKAGE                                                              #
#------------------------------------------------------------------------------------#
if [ "$CREATE_DEBIAN_PACKAGE" = "yes" ]; then
# Create .deb package

for DEBIAN_ZIP_PACKAGE in $PACKAGES_DIR/Wasabi*linux*.zip; do

# Combine paths
CURRENT_ARCH=$(echo "$DEBIAN_ZIP_PACKAGE" | grep -o 'arm64\|x64')
ZIP_PACKAGE=$(basename "$DEBIAN_ZIP_PACKAGE")

DEBIAN_PACKAGE_DIR=$BUILD_DIR/$ZIP_PACKAGE/deb
DEBIAN=$DEBIAN_PACKAGE_DIR/DEBIAN
DEBIAN_USR=$DEBIAN_PACKAGE_DIR/usr
DEBIAN_BIN=$DEBIAN_USR/local/bin

DEBIAN_ARCH_NAME=""
DEBIAN_FULL_PLATFORM_NAME="linux-x64"
DEBIAN_CONTROL_FILE_ARCHITECTURE="amd64"

if [ "$CURRENT_ARCH" = "arm64" ]; then
  DEBIAN_ARCH_NAME="-arm64"
  DEBIAN_FULL_PLATFORM_NAME="linux-arm64"
  DEBIAN_CONTROL_FILE_ARCHITECTURE="arm64"
fi


# Create necessary directories
mkdir -p $DEBIAN
mkdir -p $DEBIAN_BIN
mkdir -p $DEBIAN_USR/share/{applications,icons/hicolor}

# Copy icon files
for ICON_FILE in ./Contrib/Assets/WasabiLogo*.png; do
  SIZE=$(echo "$ICON_FILE" | grep -oP '\d+')
  ICON_DIR="$DEBIAN_USR/share/icons/hicolor/${SIZE}x${SIZE}/apps"
  mkdir -p "$ICON_DIR"
  cp "$ICON_FILE" "$ICON_DIR/$EXECUTABLE_NAME.png"
done

# Calculate package size (in kilobytes)
DEBIAN_PACKAGE_SIZE=$(du -ks --apparent-size "${BUILD_DIR}/${DEBIAN_FULL_PLATFORM_NAME}" | cut -f1)

# Create the control file content
DEBIAN_CONTROL_FILE_CONTENT="Package: ${EXECUTABLE_NAME}
Priority: optional
Section: utils
Maintainer: Wasabi Wallet Team
Version: ${VERSION}
Homepage: https://wasabiwallet.io
Vcs-Git: git://github.com/WalletWasabi/WalletWasabi.git
Vcs-Browser: https://github.com/WalletWasabi/WalletWasabi
Architecture: ${DEBIAN_CONTROL_FILE_ARCHITECTURE}
License: Open Source (MIT)
Installed-Size: ${DEBIAN_PACKAGE_SIZE}
Recommends: policykit-1
Description: open-source, non-custodial, privacy focused Bitcoin wallet
  Built-in Tor, coinjoin, and coin control features."

echo "${DEBIAN_CONTROL_FILE_CONTENT}" > $DEBIAN/control

USR_LOCAL_BIN_DIR="/usr/local/bin"
INSTALL_DIR="${USR_LOCAL_BIN_DIR}/wasabiwallet"

# Create the desktop file content
DEBIAN_DESKTOP_CONTENT="[Desktop Entry]
Type=Application
Name=Wasabi Wallet
StartupWMClass=Wasabi Wallet
GenericName=Bitcoin Wallet
Comment=Privacy focused Bitcoin wallet.
Icon=${EXECUTABLE_NAME}
Terminal=false
Exec=${EXECUTABLE_NAME}
Categories=Office;Finance;
Keywords=bitcoin;wallet;crypto;blockchain;wasabi;privacy;anon;awesome;"

# Write the content to the file
DEBIAN_DESKTOP="${DEBIAN_USR}/share/applications/${EXECUTABLE_NAME}.desktop"
echo "${DEBIAN_DESKTOP_CONTENT}" > $DEBIAN_DESKTOP
chmod 0644 $DEBIAN_DESKTOP

# Copy the build to into the debian package structure
cp -a "${BUILD_DIR}/${DEBIAN_FULL_PLATFORM_NAME}" $DEBIAN_BIN/wasabiwallet

# Create wrapper scripts
echo "#!/usr/bin/env sh
\"${INSTALL_DIR}/${EXECUTABLE_NAME}\" \"\$@\"" > ${DEBIAN_BIN}/${EXECUTABLE_NAME}

echo "#!/usr/bin/env sh
\"${INSTALL_DIR}/${EXECUTABLE_NAME}d\" \"\$@\"" > ${DEBIAN_BIN}/${EXECUTABLE_NAME}d

# Remove execution to everything except for executables and their wrapper scripts
chmod 0755 ${DEBIAN_BIN}/wasabiwallet
find ${DEBIAN_BIN}/wasabiwallet -type f -exec chmod 644 {} \;
find ${DEBIAN_BIN}/wasabiwallet -type d -not -path ${DEBIAN_BIN}/wasabiwallet -exec chmod 755 {} \;
chmod 0755 ${DEBIAN_BIN}/wasabiwallet/${EXECUTABLE_NAME}{,d}
chmod 0755 ${DEBIAN_BIN}/${EXECUTABLE_NAME}{,d}

# Make the bundled Tor binary executable
DEBIAN_BUNDLED_BINARIES="${DEBIAN_BIN}/wasabiwallet/BundledApps/Binaries/${DEBIAN_FULL_PLATFORM_NAME}"
chmod 0755 "${DEBIAN_BUNDLED_BINARIES}/Tor/tor"

if [[ "$PACKAGE_COORDINATOR" == "yes" ]]; then
  # Create wrapper scripts
  echo "#!/usr/bin/env sh
  \"${INSTALL_DIR}/${COORDINATOR_EXECUTABLE_NAME}\" \"\$@\"" > ${DEBIAN_BIN}/${COORDINATOR_EXECUTABLE_NAME}

  # Remove execution to everything except for executables and their wrapper scripts
  chmod 0755 ${DEBIAN_BIN}/wasabiwallet/${COORDINATOR_EXECUTABLE_NAME}
  chmod 0755 ${DEBIAN_BIN}/${COORDINATOR_EXECUTABLE_NAME}

fi

# Build the .deb package
dpkg-deb -Zxz --root-owner-group --build "${DEBIAN_PACKAGE_DIR}" "$PACKAGES_DIR/${PACKAGE_FILE_NAME_PREFIX}${DEBIAN_ARCH_NAME}.deb"

done
fi

#------------------------------------------------------------------------------------#
# CREATE APPIMAGE                                                                    #
#------------------------------------------------------------------------------------#
if [ "$CREATE_APPIMAGE" = "yes" ]; then

# appimagetool for x86_64 (used to build both x64 and arm64 AppImages) and the AppImage runtime for each
# architecture. Both are pinned to a release and hash checked on every run; the "continuous" tags they would
# otherwise come from are mutable.
# Hashes: gh api repos/<owner>/<repo>/releases/tags/<tag> --jq '.assets[] | "\(.name) \(.digest)"'
APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGE_RUNTIME_VERSION="20251108"
APPIMAGE_RUNTIME_X86_64_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"
APPIMAGE_RUNTIME_AARCH64_SHA256="00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444"
APPIMAGETOOL_DIR="$BUILD_DIR/appimagetool"

# download_verified <destination> <url> <sha256>
download_verified() {
  curl -fL --retry 3 --retry-delay 2 --retry-all-errors -o "$1" "$2"
  echo "$3  $1" | sha256sum -c -
}

rm -rf "$BUILD_DIR"/appimagetool*
mkdir -p "$APPIMAGETOOL_DIR"
download_verified "$APPIMAGETOOL_DIR/appimagetool-x86_64.AppImage" \
  "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" \
  "$APPIMAGETOOL_SHA256"
download_verified "$APPIMAGETOOL_DIR/runtime-x86_64" \
  "https://github.com/AppImage/type2-runtime/releases/download/$APPIMAGE_RUNTIME_VERSION/runtime-x86_64" \
  "$APPIMAGE_RUNTIME_X86_64_SHA256"
download_verified "$APPIMAGETOOL_DIR/runtime-aarch64" \
  "https://github.com/AppImage/type2-runtime/releases/download/$APPIMAGE_RUNTIME_VERSION/runtime-aarch64" \
  "$APPIMAGE_RUNTIME_AARCH64_SHA256"
chmod +x "$APPIMAGETOOL_DIR/appimagetool-x86_64.AppImage"

# Extract appimagetool (needed for environments without FUSE like Docker/CI)
pushd "$APPIMAGETOOL_DIR" || exit
./appimagetool-x86_64.AppImage --appimage-extract > /dev/null
mv squashfs-root/AppRun appimagetool
mv squashfs-root/usr .
rm -rf squashfs-root appimagetool-x86_64.AppImage
popd || exit

APPIMAGETOOL="$APPIMAGETOOL_DIR/appimagetool"

for LINUX_ZIP_PACKAGE in $PACKAGES_DIR/Wasabi*linux*.zip; do

# Determine architecture from package name
CURRENT_ARCH=$(echo "$LINUX_ZIP_PACKAGE" | grep -o 'arm64\|x64')
ZIP_PACKAGE=$(basename "$LINUX_ZIP_PACKAGE")

APPIMAGE_ARCH_NAME=""
APPIMAGE_FULL_PLATFORM_NAME="linux-x64"
# appimagetool uses x86_64 for x64 architecture
APPIMAGE_ARCH="x86_64"

if [ "$CURRENT_ARCH" = "arm64" ]; then
  APPIMAGE_ARCH_NAME="-arm64"
  APPIMAGE_FULL_PLATFORM_NAME="linux-arm64"
  APPIMAGE_ARCH="aarch64"
fi

APPIMAGE_BUILD_DIR="$BUILD_DIR/$ZIP_PACKAGE/appimage"
APPDIR="$APPIMAGE_BUILD_DIR/AppDir"
APPDIR_USR="$APPDIR/usr"
APPDIR_BIN="$APPDIR_USR/bin"

# Create AppDir structure
mkdir -p "$APPDIR_BIN"
mkdir -p "$APPDIR_USR/share/applications"
mkdir -p "$APPDIR_USR/share/icons/hicolor"

# Copy application files
cp -a "${BUILD_DIR}/${APPIMAGE_FULL_PLATFORM_NAME}/." "$APPDIR_BIN/"

# Copy icon files to AppDir
for ICON_FILE in ./Contrib/Assets/WasabiLogo*.png; do
  SIZE=$(echo "$ICON_FILE" | grep -oP '\d+')
  ICON_DIR="$APPDIR_USR/share/icons/hicolor/${SIZE}x${SIZE}/apps"
  mkdir -p "$ICON_DIR"
  cp "$ICON_FILE" "$ICON_DIR/$EXECUTABLE_NAME.png"
done

# Copy the 256x256 icon to the root of AppDir (required by AppImage spec)
cp "./Contrib/Assets/WasabiLogo256.png" "$APPDIR/$EXECUTABLE_NAME.png"

# Create the desktop file
APPIMAGE_DESKTOP_CONTENT="[Desktop Entry]
Type=Application
Name=Wasabi Wallet
StartupWMClass=Wasabi Wallet
GenericName=Bitcoin Wallet
Comment=Privacy focused Bitcoin wallet.
Icon=${EXECUTABLE_NAME}
Terminal=false
Exec=${EXECUTABLE_NAME}
Categories=Office;Finance;
Keywords=bitcoin;wallet;crypto;blockchain;wasabi;privacy;anon;awesome;"

echo "${APPIMAGE_DESKTOP_CONTENT}" > "$APPDIR/$EXECUTABLE_NAME.desktop"
chmod 0644 "$APPDIR/$EXECUTABLE_NAME.desktop"

# Also create in standard location
cp "$APPDIR/$EXECUTABLE_NAME.desktop" "$APPDIR_USR/share/applications/"

# Create the AppRun script
echo '#!/bin/sh
HERE="$(dirname "$(readlink -f "${0}")")"
export PATH="${HERE}/usr/bin:${PATH}"
exec "${HERE}/usr/bin/'"${EXECUTABLE_NAME}"'" "$@"' > "$APPDIR/AppRun"
chmod 0755 "$APPDIR/AppRun"

# Make executables executable
chmod 0755 "$APPDIR_BIN/${EXECUTABLE_NAME}"
chmod 0755 "$APPDIR_BIN/${EXECUTABLE_NAME}d"

# Set proper permissions for the rest
find "$APPDIR_BIN" -type f -name "*.so" -exec chmod 0755 {} \;
find "$APPDIR_BIN" -type f -name "*.so.*" -exec chmod 0755 {} \;

# Make bundled binaries executable
if [ -d "$APPDIR_BIN/BundledApps/Binaries/${APPIMAGE_FULL_PLATFORM_NAME}" ]; then
  find "$APPDIR_BIN/BundledApps/Binaries/${APPIMAGE_FULL_PLATFORM_NAME}" -type f -executable -exec chmod 0755 {} \;
  # tor binary
  for BINARY in tor; do
    if [ -f "$APPDIR_BIN/BundledApps/Binaries/${APPIMAGE_FULL_PLATFORM_NAME}/$BINARY" ]; then
      chmod 0755 "$APPDIR_BIN/BundledApps/Binaries/${APPIMAGE_FULL_PLATFORM_NAME}/$BINARY"
    fi
  done
fi

# Build the AppImage
APPIMAGE_FILE_NAME="${PACKAGE_FILE_NAME_PREFIX}${APPIMAGE_ARCH_NAME}.AppImage"
ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" --runtime-file "$APPIMAGETOOL_DIR/runtime-$APPIMAGE_ARCH" "$APPDIR" "$PACKAGES_DIR/$APPIMAGE_FILE_NAME"

done
fi

#------------------------------------------------------------------------------------#
# SIGN EVERYTHING                                                                    #
#------------------------------------------------------------------------------------#
if [ "$SIGN_PGP" = "yes" ]; then
pushd "$PACKAGES_DIR" || exit
for FILE in ./*; do
  sha256sum "$FILE" >> SHA256SUMS
  gpg --armor --detach-sign --output "$FILE.asc" "$FILE"
done
gpg --sign --digest-algo sha256 -a --clearsign --armor --output SHA256SUMS.asc SHA256SUMS

echo "
#r \"nuget:NBitcoin\"
open System
open System.IO
open System.Security.Cryptography
open NBitcoin

let args = Environment.GetCommandLineArgs()
let wasabiPrivateKey = Key.Parse(args[3], Network.Main)
args[2]
|> File.ReadAllBytes
|> SHA256.HashData
|> uint256
|> wasabiPrivateKey.Sign
|> _.ToDER()
|> Convert.ToBase64String
|> Console.WriteLine
" > signer.fsx
dotnet fsi signer.fsx SHA256SUMS.asc $SIGNING_WASABI_KEY > SHA256SUMS.wasabisig
rm signer.fsx

popd || exit
fi

if [ "$RELEASE_NOTE" = "yes" ]; then
  sed -e "s/{version}/$VERSION/g" \
      -e "/{highlights}/r ./WalletWasabi/Announcements/ReleaseHighlights.md" \
      -e "/{highlights}/d" \
      ./Contrib/ReleaseTemplate.md
fi
