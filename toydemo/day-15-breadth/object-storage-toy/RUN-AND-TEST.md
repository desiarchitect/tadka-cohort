# RUN-AND-TEST.md for Object Storage (S3-like) Toy

**Toy:** Object Storage Toy
**Day Introduced:** Day 15 (breadth — object storage domain primer)
**Related Curriculum:** domain-primers object storage, interview "design Dropbox/S3", Razorpay teardown (presigned uploads).
**Purpose:** Show why storing menu images as Postgres `bytea` bloats backups and forces the API to proxy bytes — vs metadata in DB + presigned direct-to-object-store URLs.

## 1. Overview & Why This Toy Exists
Engineers store files "in the database" because it's easy. At 50 menu images × 200 KB you already see ~10 MB in Postgres, slow backup/restore, and the API becomes a byte pipe. Production uses S3/GCS + presigned URLs so clients upload/download **around** the app.

**Teach with `real-demo.js` only** — real Postgres + real HTTP object server.

## 2. The Failure Scenario
**Pattern:** `bytea` column holds image bytes; mobile app hits `GET /api/menu/{id}/image` → API `SELECT data FROM ...` → streams to client.

**Bad symptoms:**
- `pg_total_relation_size` grows with every image
- App↔DB wire carries full image bytes on every view
- Connection pool memory spikes; you cannot CDN-cache DB rows

## 3. Exact Steps — BREAK
**Prerequisites:** Docker, project's Postgres.

```powershell
cd D:\work\desi-architect\tadka
docker compose up -d postgres

cd toydemo\day-15-breadth\object-storage-toy
node real-demo.js --mode=break
```

**Observe:**
- **Logical bytes ~10 MB** (`sum(octet_length(data))`) — what backups/API must account for
- On-disk size may be smaller (Postgres compresses repetitive payloads — another reason blobs belong in object store)
- **50 rows** read, **~9.8 MB** would cross app↔DB if API proxies
- `EXPLAIN ANALYZE` shows sequential read of all `bytea` values

## 4. The Fix
**Pattern:** Postgres stores `(name, object_key)` only. Object bytes live on a local object server. Client gets a **presigned URL** (HMAC token + expiry) and downloads directly.

```powershell
node real-demo.js --mode=fix
```

**Observe:**
- `menu_images_meta` size **~few KB** (metadata only)
- **~9.8 MB** fetched via HTTP from object server (bypasses Postgres)
- Presigned token rejected after expiry (403)

## 5. Verify the Fix
| Signal | break (bytea in DB) | fix (presigned object store) |
|--------|---------------------|------------------------------|
| Postgres table size | ~10 MB | ~few KB metadata |
| Bytes through DB | all image bytes | zero image bytes |
| Client download path | API → DB → client | client → object store |
| CDN offload | impossible | natural |

## 6. Full Run Instructions
```powershell
docker compose up -d postgres
cd tadka\toydemo\day-15-breadth\object-storage-toy
node real-demo.js --mode=break
node real-demo.js --mode=fix
```

**Scale:** `$env:OBJECT_COUNT=100` before running (doubles bytes moved).

**Cleanup:** `TRUNCATE menu_images_db, menu_images_meta;` or leave — tables are idempotent.

## 7. Test Cases
| Test | Command | Expect |
|------|---------|--------|
| DB bloat | `--mode=break` | ~10 MB relation size for 50 × 200 KB |
| Metadata only | `--mode=fix` | KB-scale Postgres, MB-scale HTTP |
| Presigned auth | fix fetch with bad token | 403 forbidden |

## 8. Troubleshooting
- **`No such container: tadka-postgres`** — `docker compose up -d postgres` from `tadka/`. Do not paste `#` comments on Windows.
- **Port 31211 in use** — kill prior `real-demo.js --mode=fix` or set `$env:OBJECT_PORT=31212`.
- **`objects/` folder** — auto-created on first run; gitignored.

## 9. Cross-Stack Notes
- **AWS S3 presigned URL** = same HMAC+expiry pattern at cloud scale.
- **.NET:** `IAmazonS3.GetPreSignedURL` + metadata row in EF.
- **Java:** MinIO / GCS signed URLs; never `bytea` for user uploads.

## 10. Curriculum Links
- Day 15 breadth object-storage primer
- Razorpay teardown (webhooks + object storage for receipts)
- Tadka stores order snapshots as denormalized text — not binary blobs in OLTP

## 11. Failure-First Narrative
"In a real system you'd see Postgres backups swell and API p99 spike every time someone opens a menu photo. With blobs in the database the app becomes a CDN — 50 images means ~10 MB crossing the DB wire. The fix is metadata in Postgres and presigned URLs so bytes flow client ↔ object store. After the fix Postgres stays tiny and the API never touches image bytes."

## 12. Limitations
- Local HTTP server simulates S3 — no multipart upload, no content-hash dedup (stretch).
- Does not cover virus scan pipeline or CDN cache invalidation.
- Single-tenant toy; production adds IAM, bucket policies, lifecycle rules.