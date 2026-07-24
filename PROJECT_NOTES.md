# StoryFunTime — Project Notes

Last updated: 2026-07-24

## What this app does
Parents/grandparents take a photo of a family member, the app turns it into a cartoon
avatar, then builds an illustrated children's storybook starring that person — with
AI-written or template-based text, voice narration, illustrated scenes, and (new) an
exportable video of the whole book.

## Repos
- **Backend**: `github.com/800globalenglish/StoryFunTimeApi` — C# .NET 9 Minimal API
- **Frontend**: `github.com/800globalenglish/storyfuntime` — Flutter app (`story_fun_time` folder)
- **Database**: SQL Server, `StoryFunTimeDb`

## Local folder paths
- Backend: `C:\Users\fancy\source\repos\StoryFunTimeApi`
- Frontend: `C:\Users\fancy\source\repos\story_fun_time`
(Easy to mix these up — most PowerShell errors so far have been from being in the wrong one.)

## Core architecture

### Avatars (character portraits)
- Generated via **Replicate** → **Google's `nano-banana` model** (image_input + prompt →
  edited image). This model is "Warm" (no cold-start delay), fast (~10s), and does a good
  job preserving identity from a reference photo.
- We tried InstantID first (good identity lock, but cold-starts took 1-3 min) and Grok
  Imagine (good style, poor identity consistency across regenerations) before landing on
  nano-banana for both avatars AND scenes.
- `ReplicateService.cs`: `GenerateAvatarWithNanoBanana(...)`, also still has an unused
  `CartoonizeImage` (old InstantID) method left in for reference.
- Every avatar generation (new character or regenerate) is saved to the `AvatarHistory`
  table with a unique filename — nothing overwrites in place. A gallery screen lets users
  browse/select/delete past avatars per character.
- **Photo retention**: original uploaded photos are kept permanently (needed so
  "Regenerate" can re-run from the same source photo). Decided deliberately — simplicity
  over the privacy tradeoff of deleting them.

### Scenes (per-page illustrations)
- Also via nano-banana, but as a **single call** with ALL characters' avatars as
  reference images + the page's text as the scene description
  (`GenerateSceneWithCharacters` in `ReplicateService.cs`). This replaced an earlier,
  worse two-step pipeline (Grok scene generation + a `SwapFace` step using
  `easel/advanced-face-swap`) that didn't work well.
- Consistency is good for distinctive-looking characters (glasses, distinct hair) but
  weaker for "average"-looking people — the model has less to visually latch onto.
  Prompt currently explicitly says "keep faces visible, not obscured" and "match face
  precisely, not just age/gender" to help with this.
- **KNOWN OPEN ISSUE**: there are currently two different scene-generation UI flows
  (per-page "Generate Scene" with an instructions dialog, and the new bulk
  "Generate All Scenes" with no dialog) that need to be reconciled/rationalized.

### Story text
- **AI-generated**: `GrokService.GenerateStoryPages` — xAI Grok writes N pages of story
  text based on title/theme/character names.
- **Story Templates** (newer): reusable, admin-authored stories (e.g. Bible stories) with
  `{roleName}` placeholders (e.g. `{child}`, `{grandparent}`). Applying a template to a
  book substitutes in the real character's name and creates real Page rows. Roles are
  auto-detected client-side by scanning for `{word}` patterns — no separate "define
  roles" step.
  - Backend: `StoryTemplate` + `StoryTemplatePage` models, full CRUD endpoints, plus
    `POST /books/{id}/apply-template/{templateId}`.
  - **IMPORTANT FIX (2026-07-24)**: applying a template now clears any existing pages
    on that book first — originally it just appended, causing duplicate/conflicting
    page numbers if the book already had pages.
  - Flutter: `TemplateAdminScreen` (create/edit templates + pages), `ApplyTemplateScreen`
    (pick template → map roles to your characters → apply).
  - Admin can also author templates directly via SQL if faster than the UI.

### Voice + video
- Voice recording: per-page, existing feature, unchanged.
- **Video generation (new)**: `VideoService.cs` shells out to **FFmpeg** (installed via
  `winget install ffmpeg` — not bundled with .NET, must be installed on the machine
  running the API). Per page: loops the scene image for the exact length of that page's
  audio (`-shortest`), then concatenates all page clips into one final MP4.
  Endpoint: `POST /books/{id}/generate-video`. Result stored in `Book.VideoUrl`, served
  from `wwwroot/uploads/videos/`.
  - Real-world size: ~3.8 MB for a 10-page book.
  - Flutter: "Generate Video" button → "Watch / Download Video" (opens via
    `url_launcher` in a new browser tab; browser's native player handles playback/download).
  - **Scaling note (discussed, not built)**: currently synchronous — 10 simultaneous
    video requests would all compete for CPU. If this becomes a real problem, the fix is
    a background job queue, not more code in the request handler.
  - **Storage/CDN note (discussed, not built)**: decided NOT to use a CDN (e.g. bunny.net)
    yet — premature until there's real usage. Local storage (or a second owned server)
    is fine for now, especially since videos are download-once, not streamed repeatedly.

### Character reuse across books
- A `Character` row belongs to exactly one `Book` (not a shared "Person" concept —
  we deliberately chose NOT to do a big schema rewrite for this).
- Instead: **Copy** — cloning a character's data (name, avatar, etc.) into a different
  book via `POST /books/{id}/characters/copy`. Copies do NOT count toward
  `TotalCharactersCreated` (no new AI generation happens).
- **Swap** — replace one character in a book with a different existing character
  (`ChooseDifferentCharacterScreen` — implemented as copy-in + delete-old).
- **Duplicate detection in the UI**: the Characters screen groups characters that share
  the exact same `cartoonAvatarUrl` (i.e., copies of the same person) into one tile,
  with a small "in N stories" badge, instead of showing the same face multiple times.
- Every user has one hidden "library" book (`Book.IsLibrary = true`, title "My
  Characters") — used to hold characters created via "Take Photo" before they're
  attached to any real story. Filtered out of the normal Stories list.

### Usage tracking (early credit-system groundwork)
- `UserStats` table: `TotalCharactersCreated`, `TotalCharactersDeleted` per `UserId`.
- Incremented on every avatar generation (add or regenerate) and every avatar deletion.
- **Not yet built**: an actual trial/credit limit system. Discussed idea: new users get
  ~4 free character-generation "tries" and one 3-page story before hitting a paywall —
  design only, not implemented.
- No real user accounts/auth yet — `UserId` is just a hardcoded string
  (`'test-user-1'`) throughout.

## App navigation structure (redesigned 2026-07-23)
- **Home** → two big buttons: "Go to Stories" / "New Story" (goes to Characters screen)
- **Stories** (`stories_list_screen.dart`) → list of books with thumbnails (first
  character's avatar) and titles; "+" to create a new book the old way
- **Characters** (`characters_home_screen.dart`) → THE main hub now:
  - Shows every character you've ever made (grouped/deduped as above), tap to
    select/deselect
  - "New Character" → camera/gallery chooser → `AddCharacterScreen`
  - "New Book (N)" → turns green when ≥1 selected → creates a new book, copies selected
    characters in, lands on Book Details
  - Small trash icon per tile deletes that character entirely (all books)
- **Book Details** → characters row (tap "+" to open the character picker to add
  existing characters — NOT `AddCharacterScreen` directly anymore), Generate Story,
  Use a Story Template, Generate Video, Read Book, per-page voice/scene controls,
  Generate All Scenes (bulk)
- **Camera capture**: `add_character_screen.dart` has two persistent buttons ("Take
  Photo" / "Open Gallery") at the top, with a live image preview shown immediately
  below once a photo's picked — no extra tap/popup needed.

## Conventions & gotchas (read before editing)
- **Always stop the API (red square in Visual Studio) before**: `dotnet build`,
  `dotnet ef migrations`, or any PowerShell edit to a `.cs` file. Otherwise you'll get
  "file in use" errors.
- **Two folders, easy to confuse**: backend commands need
  `cd C:\Users\fancy\source\repos\StoryFunTimeApi`; Flutter commands need
  `cd C:\Users\fancy\source\repos\story_fun_time`. Check `Test-Path` on a known file if
  unsure which folder you're in.
- **PowerShell multi-line `$content.Replace()` edits are unreliable** — whitespace/line-
  ending mismatches often cause silent no-ops (script reports success, nothing actually
  changed). Always verify with a follow-up `Select-String` check, never trust the
  "success" message alone. Shorter, single-line anchors are much more reliable than
  multi-line blocks.
- For genuinely tricky edits, safest approach is a **full-file replacement**: create the
  complete corrected file, present it for download, then
  `Move-Item -Path "$env:USERPROFILE\Downloads\filename.dart" -Destination "lib\...\filename.dart" -Force`.
- After any `.cs` model change: create + apply an EF Core migration
  (`dotnet ef migrations add X`, `dotnet ef database update`) — easy to forget the
  second step, or to forget entirely if a build error interrupted the first attempt.
- Backend port: `http://localhost:5220` (hardcoded in Flutter's `api_service.dart`
  `baseUrl`). Frontend runs on a random Chrome debug port each launch — irrelevant/normal.
- Flutter image caching: any `Image.network(...)` showing something that might change
  needs a cache-busting suffix — `?v=${DateTime.now().millisecondsSinceEpoch}` — or
  Flutter will keep showing a stale cached copy after regeneration.
- Git: two separate repos, two separate commits/pushes needed for any full-stack change.
  `.gitignore` on the backend must have `wwwroot/uploads/` UNCOMMENTED (it ships
  commented out by default) — otherwise real uploaded photos get committed. This
  happened at least twice; if it happens again, `git filter-repo` was used successfully
  both times to scrub history (approach: `git filter-repo --path <file> --invert-paths
  --force`, then `git remote add origin <url>` again since filter-repo removes it, then
  `git push --force`).
- Terminology: use "character" not "avatar" in user-facing text (renamed throughout on
  2026-07-23 for consistency, since the app is now organized around reusable characters).

## Known open issues / next steps
1. **Two scene-generation flows** (per-page with dialog vs. bulk without) need to be
   reconciled — mid-discussion, not resolved.
2. A "Generate scene" button greys out/disables if a book has zero characters attached —
   confirm this is the actual cause whenever it resurfaces, since it looks alarming
   but is working as designed.
3. Trial/credit system (~4 free character generations, first story capped at 3 pages) —
   designed in conversation, not built.
4. Video generation is synchronous — fine for now, would need a background job queue if
   many simultaneous users ever generate videos at once.
5. No real user auth — everything is hardcoded to `'test-user-1'`.
6. Multi-character scene consistency is good but not perfect for "average"-looking
   people — inherent model limitation, not a bug to fix.
