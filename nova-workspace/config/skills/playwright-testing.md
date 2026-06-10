# Playwright Testing — Red Suite UI Navigation

## Setup

The `@redbamboo/testing` package lives at `T:\Projects\redbamboo-packages\packages\testing`.
It provides `RedSuiteNavigator` for browsing any Red Suite app via Playwright.

## Port Map

| App | Backend | Frontend Dev |
|-----|---------|-------------|
| RedCompute | 18800 | — |
| CodeRed | 18801 | 18901 |
| RedMatter | 18802 | 18902 |
| Nova | 18803 | 18903 |
| RedLeaf | 18804 | 18904 |

## Running Playwright Scripts

**Use CJS with `node -e` and `require('playwright')`**. The ESM path (`tsx` with imports) hangs when run as background tasks. CJS works reliably:

```js
node -e "
const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  await page.goto('http://localhost:18904');
  // do stuff
  await page.screenshot({ path: 'screenshot.png', fullPage: true });
  await browser.close();
})().catch(e => { console.error(e.message); process.exit(1); });
"
```

Always run from the testing package directory: `cd "T:/Projects/redbamboo-packages/packages/testing"`.

## Red Suite UI Patterns

All Red Suite apps use `@redbamboo/ui` and share common UI patterns:

### Navigation
- **Top bar**: App name (left), tab buttons (center/right), hamburger menu (far right)
- **Hamburger**: `page.locator('button').filter({ has: page.locator('i.fa-bars') })` — opens dropdown with Console, Settings, Command Palette, About
- **Tabs**: Buttons in the top bar, e.g. `text=Workspace`, `text=Entities`

### Master-Detail Layout
- Left sidebar: list of items
- Right content: detail view of selected item
- Common in entity lists, settings panels, automation views

### Selector Gotchas
- **Text matching is ambiguous** when the same text appears in sidebar AND content area (e.g., "Project" appears as a sidebar item and as an instance row). Use `.first()` or `.last()` to disambiguate, or target the specific container.
- **Tab buttons** like "Schema" / "Instances" may share text with content. Use `button:has-text("Schema")` or `[role=tab]` selectors.
- **Modals** use overlay + card pattern. Wait for them with `page.waitForSelector('[class*=modal], [class*=overlay]')`.

### Timing
- After navigation clicks, wait 800-1500ms for content to load
- `page.waitForTimeout(1000)` is reliable for most transitions
- For API-dependent content, prefer `page.waitForSelector` over fixed waits

## Video Recording

Playwright can record the entire test flow as a video. Use `recordVideo` on the browser context:

```js
const context = await browser.newContext({
  recordVideo: { dir: 'videos/', size: { width: 1280, height: 720 } }
});
const page = await context.newPage();
// ... run the flow ...
const videoPath = await page.video().path();
await context.close(); // video is finalized on context close
await browser.close();
```

**Important**: the video file is only complete after `context.close()`. Call `page.video().path()` before closing to get the file path. Playwright saves as `.webm` (VP8).

### Frame Extraction with ffmpeg

Extract frames from the video to validate the flow visually (since we can view PNGs but not video inline):

```bash
# Extract one frame every 2 seconds
ffmpeg -i video.webm -vf "fps=0.5" frames/frame_%03d.png

# Extract frame at a specific timestamp
ffmpeg -i video.webm -ss 00:00:03 -frames:v 1 frame_3s.png

# Extract N evenly-spaced frames
ffmpeg -i video.webm -vf "select='not(mod(n\,30))'" -vsync vfill frames/frame_%03d.png
```

### Recommended Flow

1. **Record video** of the entire test (always-on, cheap)
2. **Take screenshots** at key checkpoints for immediate self-validation
3. **Extract frames** from the video at the end for flow validation (transitions, animations, timing)
4. **Show Laurent the video path** so he can watch the full flow
5. **Show key frames inline** using markdown image syntax for quick review

### Video + Screenshots Together

When testing, use both. Screenshots give you instant validation at specific states. Video captures everything in between: transitions, scroll behavior, loading states, timing issues.

```js
// Pattern: video context with checkpoint screenshots
const context = await browser.newContext({
  recordVideo: { dir: 'videos/', size: { width: 1280, height: 720 } }
});
const page = await context.newPage();
await page.goto('http://localhost:18904');
await page.screenshot({ path: 'screenshots/01_initial.png' });
// ... navigate ...
await page.screenshot({ path: 'screenshots/02_after_click.png' });
// ... more flow ...
const videoPath = await page.video().path();
await context.close();
await browser.close();
// Now extract frames for flow review
// execSync('ffmpeg -i ' + videoPath + ' -vf "fps=1" frames/frame_%03d.png');
```

## Viewing Screenshots & Videos

Use the Read tool to view `.png` files — they render inline. Save screenshots to the `screenshots/` directory in your workspace — Nova's chat can only display files that live inside the workspace.

When showing screenshots to Laurent, use markdown image syntax with the absolute workspace path:
```
![description](T:/Projects/nova/nova-workspace/screenshots/screenshot.png)
```

For videos, provide the file path. Laurent can open `.webm` files directly.

## RedLeaf Specific

- **Workspace tab**: Project grid with thumbnail cards + "New Project" dashed card
- **Entities tab**: Master-detail with entity types in sidebar, instances/schema in detail
- **Entity type detail**: Has "Instances" and "Schema" sub-tabs
- **New Project modal**: Triggered by clicking "New Project" card. Fields: Name (required), Description, Icon (text input)
- **Thumbnail generation**: Upload + Generate buttons on project pages. Generate opens modal with AI prompt pre-filled from project metadata.

## Running the Explore Script

```bash
cd "T:/Projects/redbamboo-packages/packages/testing"
node scripts/explore-redleaf.cjs
```

Produces timestamped, numbered screenshots in `screenshots/` covering workspace, projects, all entity types (instances + schema), menu, and settings.

## Key Selectors (from @redbamboo/ui data-slot attributes)

- Sidebar items: `[data-slot="master-detail-sidebar"] [data-slot="item-list-row"]`
- Item titles: `[data-slot="item-list-title"]`
- Content area: `[data-slot="master-detail-content"]`
- Tab triggers: `[data-slot="tabs-trigger"]`
- Nav tabs: `nav button`
- Dropdown menu items: `[data-slot="dropdown-menu-item"]`
- Project cards: `button:has(h3)`

## Checklist Before Navigating

1. Verify the app is running: `curl -s http://localhost:{port}` 
2. Run from the testing package dir
3. Use CJS (`require`), not ESM (`import`)
4. Always `await browser.close()` in a finally block
5. Don't click things that create/delete data unless explicitly told to
