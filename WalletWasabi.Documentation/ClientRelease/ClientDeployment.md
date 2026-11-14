# Prepare the release

- Edit the `WalletWasabi/Announcements/ReleaseHighlights.md` file with the most important changes.

# Release

- Make sure the commit to release builds.
- Tag the commit you want to release with format v0.0.0
- Push the tag to GitHub. The release workflow builds the packages with `Contrib/release.sh` and signs them with the PGP key in the secrets of the repository (`SIGNING_PGP_KEY`, `SIGNING_PGP_PASSPHRASE`).
- Test the packages
- Mark the release as final (remove the draft flag)
