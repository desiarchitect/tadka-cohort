#!/usr/bin/env node
/**
 * Generates real media files on disk for the HLS + CDN demo.
 *   media/progressive/full.bin  — 5 MB single file (progressive download)
 *   media/hls/playlist.m3u8     — valid HLS manifest
 *   media/hls/segNNN.ts         — 10 × 512 KB segments
 */

const fs = require('fs');
const path = require('path');

const MEDIA = path.join(__dirname, 'media');
const HLS = path.join(MEDIA, 'hls');
const SEGMENT_COUNT = 10;
const SEGMENT_BYTES = 512 * 1024;
const FULL_BYTES = SEGMENT_COUNT * SEGMENT_BYTES;

function writeChunk(filePath, size, label) {
  const chunk = Buffer.alloc(1024, label);
  const fd = fs.openSync(filePath, 'w');
  let written = 0;
  while (written < size) {
    const n = Math.min(1024, size - written);
    fs.writeSync(fd, chunk, 0, n);
    written += n;
  }
  fs.closeSync(fd);
}

fs.mkdirSync(path.join(MEDIA, 'progressive'), { recursive: true });
fs.mkdirSync(HLS, { recursive: true });

writeChunk(path.join(MEDIA, 'progressive', 'full.bin'), FULL_BYTES, 'P');
console.log(`Wrote progressive file: ${(FULL_BYTES / 1024 / 1024).toFixed(1)} MB`);

const lines = [
  '#EXTM3U',
  '#EXT-X-VERSION:3',
  '#EXT-X-TARGETDURATION:6'
];
for (let i = 0; i < SEGMENT_COUNT; i++) {
  const name = `seg${String(i).padStart(3, '0')}.ts`;
  writeChunk(path.join(HLS, name), SEGMENT_BYTES, `S${i % 10}`);
  lines.push('#EXTINF:6.0,');
  lines.push(name);
}
lines.push('#EXT-X-ENDLIST');
fs.writeFileSync(path.join(HLS, 'playlist.m3u8'), lines.join('\n') + '\n');

console.log(`Wrote HLS playlist + ${SEGMENT_COUNT} segments (${(SEGMENT_BYTES / 1024).toFixed(0)} KB each)`);
console.log('Ready: node real-demo.js --mode=break | --mode=fix');