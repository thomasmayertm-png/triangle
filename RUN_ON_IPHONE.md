# Running the app on your iPhone (no Mac needed)

The app is built for **WebGL** and runs in **Safari on the iPhone**. Finger touches
act as the mouse, so dragging and the sliders just work. Two paths below:
**(A) quick test over Wi-Fi**, and **(B) put it online** (itch.io or GitHub Pages).

---

## 0. One-time: build the WebGL version (on Windows, in Unity)

1. **Add the module** (if needed): Unity Hub → Installs → `6000.4.5f1` → gear → *Add
   Modules* → tick **WebGL Build Support** → install.
2. In Unity: **File ▸ Build Profiles / Build Settings** → select **Web / WebGL** →
   **Switch Platform**.
3. **Player Settings ▸ Publishing Settings** (matters for the simple server):
   - **Compression Format:** `Gzip` (or `Disabled`)
   - **Decompression Fallback:** **ON**  ← makes it work on any host
4. Back in Build Settings → **Build** → choose a new folder named **`WebGLBuild`** at
   the project root (`C:\Unity_projects\Triangle\WebGLBuild`).

That folder contains `index.html`, a `Build/` folder and `TemplateData/`.

---

## A. Quick test over Wi-Fi (fastest)

Phone and PC must be on the **same Wi-Fi**.

1. In a terminal at the project root:
   ```
   python tools/serve.py
   ```
   It prints a phone URL, e.g. `http://192.168.1.138:8080`.
2. First run: Windows Firewall may prompt → **Allow** on **Private** networks
   (otherwise the phone can't reach it).
3. On the iPhone, open that URL in **Safari**. Rotate to **landscape** for room.

> If the phone can't connect: confirm same Wi-Fi, that you allowed the firewall
> prompt, and that the IP shown is the `192.168.x.x` one (not a VPN address).

---

## B. Put it online (open from anywhere)

### Option B1 — itch.io (easiest, free, no git)
1. **Zip** the *contents* of `WebGLBuild` (so `index.html` is at the top of the zip).
2. itch.io → **Upload new project** → **Kind of project: HTML**.
3. Upload the zip → tick **"This file will be played in the browser."**
4. Set **Embed** → *Manually set size* ~ `1280 × 720`, and tick **Fullscreen button**.
5. Save → **View page** → open that URL in iPhone Safari.

### Option B2 — GitHub Pages (free, a URL you control)
1. Create a repo and push the build (the build output can live on a `gh-pages`
   branch or in a `/docs` folder). Simplest:
   ```
   # from a copy of the WebGLBuild folder
   git init && git add -A && git commit -m "web build"
   git branch -M gh-pages
   git remote add origin https://github.com/<you>/triangle-web.git
   git push -u origin gh-pages
   ```
2. GitHub → repo **Settings ▸ Pages** → Source: **Deploy from a branch** →
   Branch **gh-pages** / root → Save.
3. Wait ~1 min → open `https://<you>.github.io/triangle-web/` in iPhone Safari.

> GitHub Pages serves `.gz`/`.br` correctly, so either compression setting is fine.

---

### Option B3 — Auto-build with GitHub Actions (hands-off, recommended)
Two workflows are already in `.github/workflows/`. After a one-time setup, **every
push rebuilds WebGL in the cloud and republishes your Pages URL** — you never build
in Unity again.

**One-time setup:**
1. **Create the repo & push** (branch is `master`):
   ```
   git remote add origin https://github.com/<you>/triangle.git
   git push -u origin master
   ```
2. **Get a free Unity CI licence** (current game-ci method — activate locally, no
   cloud activation action; that action is deprecated):
   - **Unity Hub → (gear) Preferences → Licenses** → **Add → Get a free personal
     license**. This writes `C:\ProgramData\Unity\Unity_lic.ulf`.
   - Repo → **Settings ▸ Secrets and variables ▸ Actions** → add **three** secrets:
     - **`UNITY_LICENSE`** = the entire contents of `Unity_lic.ulf`
     - **`UNITY_EMAIL`** = your Unity account email
     - **`UNITY_PASSWORD`** = your Unity account password
3. **Turn on Pages via Actions:** repo → **Settings ▸ Pages ▸ Source = "GitHub Actions"**.

**From then on:** every `git push` runs **"Build WebGL & Deploy to Pages"**. Watch it in
the **Actions** tab; when green, your app is live at
`https://<you>.github.io/triangle/` — open it in iPhone Safari.

> Notes: the build reads your Unity version from `ProjectSettings/ProjectVersion.txt`
> automatically. The first cloud build is slow (~10-15 min, no cache yet); later ones
> are faster thanks to the Library cache. Unity WebGL uses relative paths, so it works
> fine under the `/<repo>/` sub-path Pages serves from.

## Mobile UI notes
- **A− / A+** buttons (bottom-right) scale the whole panel/fonts — tap **A+** on the
  phone to enlarge, **A−** to shrink. Always reachable regardless of current size.
- **Landscape** gives the Explain panel room beside the main panel; in **portrait**
  it stacks below and you'll see a "rotate" hint.
- The map fills the screen automatically; only the panel is scaled by A−/A+.
