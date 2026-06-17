#!/usr/bin/env node
/**
 * Real WebSocket chat with 2 instances — break (no backplane) vs fix (Redis pub/sub).
 *
 * Usage:
 *   node real-chat.js --mode=break
 *   node real-chat.js --mode=fix
 */

const http = require('http');
const WebSocket = require('ws');
const Redis = require('ioredis');

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];
const useBackplane = mode === 'fix';

const ROOM = 'bangalore-foodies';
const CHANNEL = `toydemo:ws:room:${ROOM}`;
const PORTS = [31101, 31102];
const CLIENTS_PER_SERVER = 25;
const MESSAGES = 50;

const rooms = new Map(); // instanceId -> Map(room -> Set(ws))

function trackConnection(instanceId, ws, room) {
  if (!rooms.has(instanceId)) rooms.set(instanceId, new Map());
  const instRooms = rooms.get(instanceId);
  if (!instRooms.has(room)) instRooms.set(room, new Set());
  instRooms.get(room).add(ws);
  ws._room = room;
  ws._instanceId = instanceId;
}

function untrack(ws) {
  const instRooms = rooms.get(ws._instanceId);
  if (instRooms && ws._room) {
    const set = instRooms.get(ws._room);
    if (set) set.delete(ws);
  }
}

function localBroadcast(instanceId, room, payload, exceptWs = null) {
  const set = rooms.get(instanceId)?.get(room);
  if (!set) return 0;
  let n = 0;
  for (const client of set) {
    if (client !== exceptWs && client.readyState === WebSocket.OPEN) {
      client.send(payload);
      n += 1;
    }
  }
  return n;
}

function createServer(instanceId, port, redisSub, redisPub) {
  const server = http.createServer();
  const wss = new WebSocket.Server({ server });

  wss.on('connection', (ws) => {
    ws.on('message', (raw) => {
      let msg;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        return;
      }

      if (msg.type === 'join') {
        trackConnection(instanceId, ws, msg.room || ROOM);
        return;
      }

      if (msg.type === 'chat') {
        const payload = JSON.stringify({
          type: 'chat',
          from: msg.from,
          text: msg.text,
          instanceId
        });

        if (useBackplane && redisPub) {
          redisPub.publish(CHANNEL, payload);
        } else {
          localBroadcast(instanceId, ROOM, payload);
        }
      }
    });

    ws.on('close', () => untrack(ws));
  });

  if (useBackplane && redisSub) {
    redisSub.subscribe(CHANNEL, (err) => {
      if (err) throw err;
    });
    redisSub.on('message', (_ch, payload) => {
      localBroadcast(instanceId, ROOM, payload);
    });
  }

  return new Promise((resolve) => {
    server.listen(port, '127.0.0.1', () => resolve({ server, wss }));
  });
}

function connectClient(url, userId) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    const received = [];

    ws.on('open', () => {
      ws.send(JSON.stringify({ type: 'join', room: ROOM, from: userId }));
      resolve({
        ws,
        userId,
        received,
        sendChat(text) {
          ws.send(JSON.stringify({ type: 'chat', from: userId, text }));
        },
        close() {
          ws.close();
        }
      });
    });

    ws.on('message', (data) => {
      const msg = JSON.parse(data.toString());
      if (msg.from !== userId) received.push(msg);
    });

    ws.on('error', reject);
  });
}

function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function main() {
  console.log('=== Stateful WebSocket Toy — Real Chat ===\n');
  console.log(`Mode: ${mode} (backplane: ${useBackplane ? 'Redis pub/sub' : 'OFF'})`);
  if (useBackplane) console.log('Prerequisite: docker compose up -d redis\n');

  let redisPub = null;
  let redisSub1 = null;
  let redisSub2 = null;

  if (useBackplane) {
    redisPub = new Redis({ host: '127.0.0.1', port: 6379 });
    redisSub1 = new Redis({ host: '127.0.0.1', port: 6379 });
    redisSub2 = new Redis({ host: '127.0.0.1', port: 6379 });
    await redisPub.ping();
  }

  const srv1 = await createServer(1, PORTS[0], redisSub1, redisPub);
  const srv2 = await createServer(2, PORTS[1], redisSub2, redisPub);

  const clients = [];
  for (let i = 0; i < CLIENTS_PER_SERVER; i++) {
    clients.push(await connectClient(`ws://127.0.0.1:${PORTS[0]}`, `user-a${i}`));
    clients.push(await connectClient(`ws://127.0.0.1:${PORTS[1]}`, `user-b${i}`));
  }
  await sleep(200);

  const senders = clients.filter((_, idx) => idx % 5 === 0);
  for (let m = 0; m < MESSAGES; m++) {
    senders[m % senders.length].sendChat(`msg-${m}`);
  }
  await sleep(500);

  let totalReceived = 0;
  for (const c of clients) {
    totalReceived += c.received.length;
  }

  const expected = MESSAGES * (clients.length - 1);
  const rate = ((totalReceived / expected) * 100).toFixed(1);

  console.log(`Clients:             ${clients.length} (${CLIENTS_PER_SERVER} per instance)`);
  console.log(`Messages sent:         ${MESSAGES}`);
  console.log(`Expected deliveries: ${expected}`);
  console.log(`Actual deliveries:   ${totalReceived}`);
  console.log(`Delivery rate:       ${rate}%`);

  for (const c of clients) c.close();
  srv1.server.close();
  srv2.server.close();
  if (redisPub) redisPub.disconnect();
  if (redisSub1) redisSub1.disconnect();
  if (redisSub2) redisSub2.disconnect();

  console.log('\nBreak mode ~50% on 2 instances; fix mode ~100%.');
}

main().catch(err => {
  console.error('Error:', err.message || err);
  process.exit(1);
});