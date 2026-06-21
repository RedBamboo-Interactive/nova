// migrate-workspace.js
// One-time data migration: creates RedLeaf page entities for Nova's memory + skills files.
// Idempotent — safe to run multiple times (PUT upserts by slug).
//
// Local requests to RedLeaf are auto-authenticated as admin — no token needed.

import fs from 'fs';
import path from 'path';

const BASE_URL = 'http://localhost:18804';
const WORKSPACE_ROOT_ID = '2de699a8-1709-4a96-9438-cbc45e79bf3e';
const WORKSPACE_DIR = path.resolve('T:/Projects/nova/nova-workspace');
const MEMORY_DIR = path.join(WORKSPACE_DIR, 'memory');
const SKILLS_DIR = path.join(WORKSPACE_DIR, 'config', 'skills');

// Directories to exclude under memory/ (top-level only)
const EXCLUDE_DIRS = new Set(['backup', 'temp', 'conversations', 'topics']);

// ── HTTP helpers ─────────────────────────────────────────────────────────────

async function upsertPage(slug, name, parentId, content = null) {
  const body = {
    name,
    type_slug: 'page',
    data: {
      parent: parentId,
      ...(content !== null ? { content } : {}),
    },
  };

  const res = await fetch(`${BASE_URL}/api/entities/by-slug/${encodeURIComponent(slug)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    const text = await res.text();
    throw new Error(`PUT ${slug} → ${res.status} ${text}`);
  }

  const data = await res.json();
  return data.id;
}

// ── File discovery ────────────────────────────────────────────────────────────

function readFilesRecursive(dir, relBase, excludeTopLevel = new Set()) {
  const results = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (excludeTopLevel.has(entry.name)) continue;
      const subDir = path.join(dir, entry.name);
      const subRel = relBase ? `${relBase}/${entry.name}` : entry.name;
      results.push(...readFilesRecursive(subDir, subRel));
    } else if (entry.isFile()) {
      const ext = path.extname(entry.name).toLowerCase();
      if (ext === '.md' || ext === '.json') {
        results.push({
          fullPath: path.join(dir, entry.name),
          relPath: relBase ? `${relBase}/${entry.name}` : entry.name,
        });
      }
    }
  }
  return results;
}

// ── Slug helpers ──────────────────────────────────────────────────────────────

// nova-ws-{segment1}-{segment2}-... with each segment's slashes replaced by dashes
function toSlug(...segments) {
  const parts = segments
    .flatMap(s => s.replace(/\\/g, '/').split('/'))
    .filter(Boolean);
  return `nova-ws-${parts.join('-')}`;
}

function baseName(filePath) {
  return path.basename(filePath, path.extname(filePath));
}

// ── Main ─────────────────────────────────────────────────────────────────────

async function main() {
  // Map of slug → entity ID for parent lookups
  const ids = {};
  ids['nova-workspace'] = WORKSPACE_ROOT_ID;

  async function createFolder(slug, name, parentSlug) {
    const parentId = ids[parentSlug];
    if (!parentId) throw new Error(`Parent not found: ${parentSlug}`);
    const id = await upsertPage(slug, name, parentId);
    ids[slug] = id;
    console.log(`  [folder] ${slug}  (${id})`);
    return id;
  }

  async function createContentPage(slug, name, parentSlug, content) {
    const parentId = ids[parentSlug];
    if (!parentId) {
      console.error(`  [SKIP]   ${slug} — unknown parent ${parentSlug}`);
      return;
    }
    try {
      const id = await upsertPage(slug, name, parentId, content);
      ids[slug] = id;
      console.log(`  [page]   ${slug}`);
    } catch (err) {
      console.error(`  [ERROR]  ${slug}: ${err.message}`);
    }
  }

  // ── 1. Folder structure ───────────────────────────────────────────────────

  console.log('Creating folder pages...');
  await createFolder('nova-ws-memory',                  'memory',  'nova-workspace');
  await createFolder('nova-ws-skills',                  'skills',  'nova-workspace');
  await createFolder('nova-ws-memory-projects',         'projects','nova-ws-memory');
  await createFolder('nova-ws-memory-meta',             'meta',    'nova-ws-memory');
  await createFolder('nova-ws-memory-dreaming',         'dreaming','nova-ws-memory');
  await createFolder('nova-ws-memory-dreaming-harvest', 'harvest', 'nova-ws-memory-dreaming');
  await createFolder('nova-ws-memory-dreaming-ideas',   'ideas',   'nova-ws-memory-dreaming');
  console.log('');

  // ── 2. Memory content pages ───────────────────────────────────────────────

  console.log('Creating memory content pages...');
  const memoryFiles = readFilesRecursive(MEMORY_DIR, 'memory', EXCLUDE_DIRS);

  for (const { fullPath, relPath } of memoryFiles) {
    // relPath: "memory/index.md", "memory/projects/redleaf.md", etc.
    const segments = relPath.replace(/\\/g, '/').split('/');
    const fileName = segments[segments.length - 1];
    const name = baseName(fileName);

    // Slug: nova-ws-{all segments, last one stripped of extension}
    const slugSegments = [...segments.slice(0, -1), name];
    const slug = toSlug(...slugSegments);

    // Parent slug = folder for the directory containing this file
    const parentSlug = segments.length === 2
      ? 'nova-ws-memory'                      // direct child of memory/
      : toSlug(...segments.slice(0, -1));     // e.g. nova-ws-memory-projects

    const content = fs.readFileSync(fullPath, 'utf8');
    await createContentPage(slug, name, parentSlug, content);
  }
  console.log('');

  // ── 3. Skills content pages ───────────────────────────────────────────────

  console.log('Creating skills content pages...');
  const skillsFiles = fs.readdirSync(SKILLS_DIR, { withFileTypes: true })
    .filter(e => e.isFile() && e.name.endsWith('.md'))
    .map(e => ({ fullPath: path.join(SKILLS_DIR, e.name), fileName: e.name }));

  for (const { fullPath, fileName } of skillsFiles) {
    const name = baseName(fileName);
    const slug = toSlug('skills', name);
    const content = fs.readFileSync(fullPath, 'utf8');
    await createContentPage(slug, name, 'nova-ws-skills', content);
  }
  console.log('');

  console.log('Migration complete.');
  console.log(`Total slugs tracked: ${Object.keys(ids).length}`);
}

main().catch(err => {
  console.error('\nFatal:', err.message);
  process.exit(1);
});
