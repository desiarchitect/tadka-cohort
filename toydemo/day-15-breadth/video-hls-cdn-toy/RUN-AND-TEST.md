# RUN-AND-TEST.md for Video HLS + CDN Toy

**Toy:** Video HLS + CDN Toy
**Day Introduced:** Day 15 (YouTube/Netflix domain primer)
**Purpose:** Real HTTP files + origin/edge servers — progressive 5 MB download per viewer vs HLS segments cached at edge.

## Prerequisites
Node.js v18+. No Docker. **No JS simulation** — this toy only runs real HTTP.

## Full Run (primary path)
```powershell
cd tadka\toydemo\day-15-breadth\video-hls-cdn-toy
node setup-media.js
node real-demo.js --mode=break
node real-demo.js --mode=fix
```

## What to observe

### break (progressive)
- 50 viewers × 5 MB = **~250 MB origin egress**
- Origin requests: **50**

### fix (HLS + edge)
- 50 viewers × 3 segments × 512 KB ≈ **~75 MB client bytes**
- Origin requests: **~3** (only edge cache misses for unique segments)
- Edge cache hits: **~147** (after warm segments)

## Failure narrative
Progressive download makes every viewer pay full file cost at origin. HLS segments + CDN edge collapses origin load to unique segments.

## Limitations
Fake video bytes (not playable in VLC); teaches **byte economics** and cache hit ratio, not encoding. Real ffmpeg pipeline is optional stretch.

## Curriculum
Day 15 breadth, domain-primers video, interview "design YouTube".