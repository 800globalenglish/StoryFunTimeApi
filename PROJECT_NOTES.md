# StoryFunTime — Project Notes

Last updated: 2026-07-25 (covers 3 sessions: initial build, feature session, deployment + restructure session)

## What this app does
Parents/grandparents take a photo of a family member, the app turns it into a cartoon
avatar, then builds an illustrated children's storybook starring that person — with
AI-written or template-based text, voice narration, illustrated scenes, and an
exportable video of the whole book. **Now live at https://www.storyfuntime.com**
(public root shows a "Coming Soon" page; the actual app is at `/go/` during testing).

## Repos
- **Backend**: `github.com/800globalenglish/StoryFunTimeApi` — C# .NET 9 Minimal API
- **Frontend**: `github.com/800globalenglish/storyfuntime` — Flutter app (`story_fun_time` folder)
- **Database**: SQL Server, `StoryFunTimeDb`

## Local folder paths
- Backend: `C:\Users\fancy\source\repos\StoryFunTimeApi`
- Frontend: `C:\Users\fancy\source\repos\story_fun_time`

## PRODUCTION SERVER (new this session)
- Windows Server, RDP access, shared with an existing unrelated website
- Backend cloned to `C:\StoryFunTime\StoryFunTimeApi`, published to
  `C:\inetpub\wwwroot\StoryFunTime` (IIS site "StoryFunTime", dedicated IP
  `103.90.161.169`, port 80/443) → `https://api.storyfuntime.com`
- Frontend built and deployed to `C:\inetpub\wwwroot\StoryFunTimeWeb` (IIS site
  "StoryFunTimeWeb", dedicated IP `103.90.161.122`, port 80/443) → root domain shows
  `coming-soon.html`; real app lives at `/go/` subfolder (`--base-href /go/` build)
- Both sites use a **Cloudflare Origin Certificate** (covers `storyfuntime.com` +
  `*.storyfuntime.com`) imported into `Cert:\LocalMachine\My`, bound via IIS
- DNS (Cloudflare, proxied/orange-cloud): `@` and `www` → `.122` (Flutter);
  `api` → `.169` (backend)
- SQL Server instance name: `SQLEXPRESS` (connection string:
  `Server=localhost\SQLEXPRESS;...`)
- IIS runs the site under **`IIS APPPOOL\DefaultAppPool`** (not a dedicated pool,
  despite `New-Website` sometimes implying otherwise — confirm via
  `Get-Website | Select ApplicationPool` if in doubt)

### Deployment workflow (quick reference)
**Frontend:**
```powershell
# Local:
flutter build web --base-href /go/
Compress-Archive -Path "build\web\*" -DestinationPath "C:\Users\fancy\Desktop\flutter-web-go.zip" -Force
# Copy zip to server via RDP clipboard (Ctrl+C local Desktop, Ctrl+V RDP Desktop)
# Server:
Expand-Archive -Path "C:\Users\Administrator\Desktop\flutter-web-go.zip" -DestinationPath "C:\inetpub\wwwroot\StoryFunTimeWeb\go" -Force
```
**IMPORTANT: after every frontend deploy, purge Cloudflare cache** (Cloudflare
dashboard -> Caching -> Configuration -> Purge Everything), otherwise Cloudflare keeps
serving the old `main.dart.js` for hours (saw a `Cache-Control: max-age=14400` header).
A same-file purge isn't always enough - full purge is more reliable, since Flutter's
service worker + several interlocking files need to update together. Browser-side,
a full DevTools -> Application -> Storage -> "Clear site data" also helps rule out
client caching when troubleshooting.

**Backend:**
```powershell
# Local:
git add . ; git commit -m "..." ; git push
# Server:
cd C:\StoryFunTime\StoryFunTimeApi
git pull
Import-Module WebAdministration
Stop-WebAppPool -Name "DefaultAppPool"   # releases the locked .dll
Start-Sleep -Seconds 3
dotnet publish -c Release -o C:\inetpub\wwwroot\StoryFunTime
Start-WebAppPool -Name "DefaultAppPool"
Start-Website -Name "StoryFunTime"
```
No Cloudflare purge needed for backend-only changes (dynamic API responses aren't
cached the same way).

### Deployment gotchas hit and fixed
- **IIS App Pool needs an actual SQL login** - `Trusted_Connection=True` relies on
  Windows Auth using the process identity (`IIS APPPOOL\DefaultAppPool`), which has
  no SQL access by default. Fixed via:
  `sqlcmd -S localhost\SQLEXPRESS -Q "CREATE LOGIN [IIS APPPOOL\DefaultAppPool] FROM WINDOWS; USE StoryFunTimeDb; CREATE USER [IIS APPPOOL\DefaultAppPool] FOR LOGIN [IIS APPPOOL\DefaultAppPool]; ALTER ROLE db_owner ADD MEMBER [IIS APPPOOL\DefaultAppPool];"`
- **IIS App Pool also needs actual folder write permissions** for `wwwroot/uploads/`
  even under `inetpub` - granted via
  `icacls "C:\inetpub\wwwroot\StoryFunTime\wwwroot" /grant "IIS AppPool\DefaultAppPool:(OI)(CI)M" /T`
- **`wwwroot` folder itself doesn't exist on a fresh clone** (gitignored) - must
  `mkdir` it manually after every fresh clone/publish to a new location.
- Windows PowerShell (5.1, default) vs **PowerShell 7** (`pwsh`) matters:
  `-SkipCertificateCheck` and some TLS negotiation only work correctly in PS7.
  `WebAdministration` (IIS management) module works reliably in Windows PowerShell
  but has issues in PS7 even with `-SkipEditionCheck` - use old PowerShell for IIS
  admin commands, PS7 for HTTPS testing.
- **PATH changes require a full sign-out/sign-in**, not just closing/reopening a
  PowerShell window (which can still inherit a stale environment from the same
  desktop session).
- **FFmpeg source matters for Whisper support**: gyan.dev builds include
  `--enable-whisper`; BtbN's GitHub builds **disabled Whisper as of ~June 21, 2026**
  (their own release notes cite disk/size constraints on their CI). If gyan.dev's
  direct download is blocked from a given server (seen: consistent 503s, likely a
  datacenter-IP block), download locally where it works, then transfer via RDP
  clipboard (zip the winget package folder, copy/paste through the RDP session).
  When multiple FFmpeg installs exist in PATH, order matters - Windows uses the
  first match; remove or reorder stale PATH entries if the wrong build gets picked up.

## Core architecture

### Avatars (character portraits)
- Generated via **Replicate** -> **Google's `nano-banana` model**. Fast (~10s), "Warm"
  (no cold-start), good identity preservation from a reference photo.
- `ReplicateService.cs`: `GenerateAvatarWithNanoBanana(...)` - now wraps a private
  `...Attempt` method with **automatic retry (3 attempts, 2s pause between)**, since
  generative AI has a real non-zero per-call failure rate; a single failure is no
  longer surfaced to the user as an error if a retry succeeds.
- Every avatar generation is saved to `AvatarHistory` with a unique filename; gallery
  screen lets users browse/select/delete past avatars per character.
- Original uploaded photos are kept permanently (needed for "Regenerate").

### Scenes (per-page illustrations)
- Also nano-banana, single call with all characters' avatars as reference images.
- `GenerateSceneWithCharacters(...)` also now has the same 3-attempt retry wrapper.
- Prompt now explicitly says to keep each character's **clothing and accessories**
  (hat, glasses, etc.) consistent with their reference image, not just face/hair/skin.
- Consistency remains weaker for "average"-looking people - inherent model limitation.

### Story text
- **AI-generated**: `GrokService.GenerateStoryPages` - now accepts an optional
  `extraInstructions` parameter (used both for initial generation and for
  "Regenerate text" on an individual page, which now pops up asking what to change
  before regenerating, matching how "Generate Scene" already worked).
- **Story Templates**: reusable, admin-authored stories with `{roleName}`
  placeholders. Applying a template clears existing pages first (fixed a bug where
  it used to just append, causing duplicate page numbers).
- `POST /books/{id}/generate-script` now also accepts `Title` and `Theme` directly
  in the request body and **updates the book's stored Title/Theme** when provided -
  this lets the "Generate Story" step collect title/theme/scene-count in one place
  rather than needing them set earlier at book-creation time.

### Voice + transcription (new this session)
- Recording voice on a page **automatically transcribes it into that page's text**
  using **local, free Whisper speech-to-text via FFmpeg's `af_whisper` filter** -
  no cloud API, no per-use cost. `TranscriptionService.cs` shells out to FFmpeg;
  wired into the existing `/pages/{id}/audio` upload endpoint (transcription failure
  doesn't block the audio save itself, wrapped in try/catch).
  - Needs a downloaded Whisper model file (e.g. `ggml-base.en.bin`, ~140MB) - kept
    in a project-relative `whisper-models/` folder (must stay in `.gitignore`,
    exceeds GitHub's 100MB limit; already had to `git filter-repo` once to remove
    an accidentally-committed copy from history).
  - Accuracy is decent-for-a-draft but not perfect on the smallest ("base") model -
    a few word-level errors are normal; existing "Edit text" feature is the natural
    cleanup step. Larger models would improve accuracy at the cost of speed -
    not yet tested.
  - **Windows path gotcha**: FFmpeg filter option strings (`model=...:destination=...`)
    break on Windows absolute paths containing drive-letter colons and backslashes
    (`C:\Users\...`) - the colon collides with the filter syntax's own colon
    delimiters. Fix: always use **relative paths with forward slashes** for any
    path passed inside an `-af` filter string (both the model path and any temp
    output file path).
- Play/Pause toggle added to the voice recorder's playback button (previously
  play-only, with no way to stop/pause once started).

### Video generation
- `VideoService.cs` shells out to FFmpeg: per page, loops the scene image for the
  exact length of that page's audio (`-shortest`), then concatenates all page clips
  into one final MP4. `POST /books/{id}/generate-video`, result in `Book.VideoUrl`,
  served from `wwwroot/uploads/videos/`.
- Real-world size: ~3.8 MB for a 10-page book.
- "Generate Video" / "Watch / Download Video" button now lives on Creator Wizard
  (see navigation section below), revealed once every page is complete.
- Scaling/CDN notes from discussion (not built, decided against for now): keep
  synchronous generation and local file storage until real usage numbers justify a
  background job queue or CDN - premature otherwise. Download-once (not streamed)
  usage pattern keeps bandwidth costs low even at scale.

### Character reuse across books
- Same as before: `Character` belongs to one `Book`; reuse via **Copy** (clone into
  another book) or **Swap** (replace one character with another, copy-in + delete-old).
- Duplicate-avatar grouping (same `cartoonAvatarUrl` = same person, shown once with
  an "in N stories" badge) is applied on **both** `characters_home_screen.dart` and
  `character_picker_screen.dart` - these are two separate screens, a fix applied to
  one does not automatically apply to the other.

## App navigation structure (RESTRUCTURED this session - read carefully, this changed)

Three-screen split for the book-creation/editing flow, replacing the old single
"Book Details" screen that had everything on it:

1. **Book Details** (`book_detail_screen.dart`) - the "setup" screen, shown when a
   book has **zero pages**. Just: Characters row (+ Add), "Generate Story" button
   (tapping it expands an **inline** form right there - Book Title, a real # Scenes
   dropdown 1-10, Theme, then "Generate" - no separate screen/dialog), and "Story
   Templates" button. Successfully generating (either path) **navigates forward**
   into Creator Wizard.

2. **Creator Wizard** (`creator_wizard_screen.dart`, NEW) - the dedicated
   page-by-page production workspace. Just: "Generate All Screens" (bulk scene
   generation) + the pages list (each page: text, "Generate scene" **then** "Record
   voice" - this order was deliberately swapped from the original build - plus an
   edit-menu with "Edit text" / "Regenerate text", the latter now asking for
   optional instructions first). **Once every page has both a scene and voice
   recorded**, two buttons appear at the top: "Read Book" and "Generate Video".

3. **Book Summary** (`book_summary_screen.dart`, NEW) - shown when returning to a
   book that **already has pages** (e.g. tapping it from "My Story Books"). Just:
   "Read Book" / "Generate Video" (or "Watch/Download Video" if already made) /
   "Change Book" (-> navigates into Creator Wizard to keep working).

**The routing rule**: tapping a book from `stories_list_screen.dart` fetches the
book first, then checks `book.pages.isEmpty` - empty -> Book Details (setup);
non-empty -> Book Summary. This required adding an actual API call before navigating
(previously it navigated directly without checking).

- `create_book_screen.dart`: when characters are pre-selected (always true now,
  coming from the Characters screen's "New Book" button), it **skips its own
  title/theme form entirely** - creates the book with placeholder title/theme
  behind a loading spinner, copies characters in, and lands straight on Book
  Details. The form only shows in the (currently unused) no-preselection path.
- `api_service.dart`'s `baseUrl` now **auto-switches** based on `kReleaseMode` from
  `package:flutter/foundation.dart` - `flutter run` (debug) always uses
  `http://localhost:5220`; `flutter build web` (release) always uses
  `https://api.storyfuntime.com`. No more manually editing this constant back and
  forth between local dev and deployment.

## Conventions & gotchas (read before editing)
- **Always stop the API before**: local `dotnet build`/migrations (Visual Studio
  red square); on the **server**, `Stop-WebAppPool -Name "DefaultAppPool"` before
  `dotnet publish` (IIS locks the running `.dll`).
- **Two local folders, easy to confuse**: backend =
  `C:\Users\fancy\source\repos\StoryFunTimeApi`; frontend =
  `C:\Users\fancy\source\repos\story_fun_time`. On the **server**, also easy to
  confuse `C:\Users\Administrator\...` (wrong) vs the actual project folders - the
  fastest tell is the PowerShell prompt itself; run `whoami` if ever unsure whether
  you're on local vs RDP.
- **PowerShell multi-line `$content.Replace()` edits are unreliable** - frequently
  silent no-ops from whitespace/line-ending mismatches. Always verify with a
  follow-up `Select-String`, never trust the "success" message alone. Short,
  single-line anchors are far more reliable than multi-line blocks. For big/risky
  edits, a full-file replacement (create the complete corrected file, present for
  download, `Move-Item` from Downloads) is safest.
- After any `.cs` model change: create + apply an EF Core migration on **both**
  local and production databases separately (they're two different databases).
- Flutter image caching: any `Image.network(...)` showing something that might
  change needs a cache-busting suffix (`?v=${DateTime.now().millisecondsSinceEpoch}`).
- Git: two separate repos, two separate commits/pushes. `.gitignore` must exclude
  `wwwroot/uploads/` (backend) and any large model files (`whisper-models/`) -
  `git filter-repo --path <file> --invert-paths --force` is the proven fix if
  something large/sensitive gets committed by accident (re-add `origin` remote
  afterward, since filter-repo removes it; `git push --set-upstream origin main`
  if push complains about no upstream branch afterward too).
- Terminology: "character" not "avatar" in user-facing text.

## Known open issues / next steps
1. No real user auth - everything hardcoded to `'test-user-1'`.
2. Trial/credit system (~4 free character generations, first story capped at 3
   pages) - designed in conversation, not built.
3. Whisper transcription accuracy is base-model-decent, not perfect - untested
   whether a larger model meaningfully improves it (tradeoff: slower).
4. Background job queue for video generation - not needed yet, revisit if/when
   concurrent usage becomes real.
5. Visual styling (the pink/purple gradient button look from the user's own
   flow mockups) - explicitly deferred; only flow/sequence was in scope this
   session, not visual polish.
