# Online backup to GitHub

Genesis can back each project up to your GitHub account, and restore it from there.

## Sign in (once per Windows user)

Preferences › General › **GitHub**:

- **Sign in with browser** uses Git Credential Manager, which comes with Git for Windows. It uses the
  GitHub sign-in git already has on this PC, or opens GitHub's sign-in page.
- Or paste a **personal access token** (classic tokens need the `repo` scope; fine-grained tokens need
  read and write access to repository contents and administration, so Genesis can create the
  backup repository).

The token is stored encrypted for your Windows account (DPAPI) in
`%LocalAppData%\Genesis Studio\github-token.bin`, never inside a project.

## Back a project up

Preferences › Project › **Online backup**:

- **Back up this project to GitHub**: once ticked, every **Save Project** also backs the project up,
  in the background. The status bar shows the progress.
- **Back up now** runs one backup straight away.
- Every project goes in one private repository, `github.com/<you>/Genesis-Projects`, in a folder
  named after the project.
- A backup sends only what changed: at most 10 MB per round, one commit per round, with a
  20-second pause between rounds. A backup that is interrupted carries on from the last finished
  round next time.

## What is skipped

**Choose what is backed up…** lists everything the backup skips and why. The list is the
project's `.gitignore`, shown as a list rather than text.

- **Always skipped**: caches, builds, debug output (`.genesis/Cache/`, `Debug/`, `Exports/` and so on).
- **Skipped automatically**: files over 10 MB, and model files (`.fbx`, `.obj`, `.glb`, `.gltf`,
  `.blend` and similar). Choose **Back up anyway** to include one; it leaves the `.gitignore`.
  **Skip again** puts it back.
- **Added by you**: use **Add folder…** or **Add file…** to skip more. **Remove from list** takes an
  entry out.
- GitHub refuses files over 100 MB, so those are never uploaded.

## Restore

**Restore from GitHub** appears once the project has a backup. Studio closes the project, makes
its folder match the backup and opens it again. Files the restore replaces or removes are moved
to `.genesis/Backup/Before restore <date>/` first. Skipped files (large files, models, your
entries) are left alone.

## Rename a project

Preferences › Project › **Project name**: type the new name and choose **Rename project**. Studio
closes the project and renames its folder, its `.genesisproj` file and the name inside it, and the
Hub's recent list. If the project is backed up, it also renames the project's folder on GitHub in
one commit. Then Studio opens the project again.
