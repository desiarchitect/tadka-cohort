#!/usr/bin/env node
/**
 * Stateful WebSocket / Realtime Toy (failure-first demo)
 *
 * Scenario: WhatsApp-style group chat behind a load balancer.
 * 2 WS server instances, users round-robin'd. Same room, split across instances.
 *
 * BREAK — in-memory only (no backplane): messages stay on the sender's instance.
 * FIX   — Redis pub/sub backplane: every instance hears every room message.
 *
 * Tadka Day 6 uses SSE (server→client) + Redis pub/sub for order tracking.
 * Chat needs bidirectional WS + cross-instance fan-out — this toy shows why.
 *
 * Real WebSockets: node real-chat.js --mode=break / --mode=fix
 */

const args = process.argv.slice(2);
const mode = (args.find(a => a.startsWith('--mode=')) || '--mode=break').split('=')[1];

const INSTANCES = 2;
const USERS_PER_INSTANCE = 50;
const TOTAL_USERS = INSTANCES * USERS_PER_INSTANCE;
const MESSAGES = 200;
const ROOM = 'bangalore-foodies';

function buildTopology() {
  const users = [];
  for (let i = 0; i < TOTAL_USERS; i++) {
    users.push({
      id: `user-${i + 1}`,
      instance: i % INSTANCES
    });
  }
  return users;
}

function simulateDelivery(users, useBackplane) {
  const byInstance = Array.from({ length: INSTANCES }, () => []);
  for (const u of users) byInstance[u.instance].push(u.id);

  let delivered = 0;
  let expected = 0;
  const crossInstanceMisses = [];

  for (let m = 0; m < MESSAGES; m++) {
    const sender = users[m % users.length];
    const recipients = users.filter(u => u.id !== sender.id);
    expected += recipients.length;

    if (useBackplane) {
      delivered += recipients.length;
    } else {
      const local = recipients.filter(u => u.instance === sender.instance);
      delivered += local.length;
      const missed = recipients.length - local.length;
      if (missed > 0) crossInstanceMisses.push(missed);
    }
  }

  const deliveryRate = ((delivered / expected) * 100).toFixed(1);
  const avgMissPerMsg = crossInstanceMisses.length
    ? (crossInstanceMisses.reduce((a, b) => a + b, 0) / crossInstanceMisses.length).toFixed(0)
    : 0;

  return {
    label: useBackplane
      ? 'Redis pub/sub backplane (all instances subscribed)'
      : 'In-memory only (no cross-instance fan-out)',
    instances: INSTANCES,
    users: TOTAL_USERS,
    room: ROOM,
    messagesSent: MESSAGES,
    expectedDeliveries: expected,
    actualDeliveries: delivered,
    deliveryRatePct: deliveryRate,
    avgMissedRecipientsPerMsg: avgMissPerMsg,
    usersPerInstance: USERS_PER_INSTANCE
  };
}

function printResult(r) {
  console.log(`Approach:              ${r.label}`);
  console.log(`Topology:              ${r.instances} WS instances × ${r.usersPerInstance} users`);
  console.log(`Room:                  ${r.room} (${r.users} members)`);
  console.log(`Messages sent:         ${r.messagesSent}`);
  console.log(`Expected deliveries:   ${r.expectedDeliveries} (${r.messagesSent} msgs × ${r.users - 1} recipients)`);
  console.log(`Actual deliveries:     ${r.actualDeliveries}`);
  console.log(`Delivery rate:         ${r.deliveryRatePct}%`);
  if (!r.label.includes('backplane')) {
    console.log(`Avg missed/msg:        ${r.avgMissedRecipientsPerMsg} recipients on other instances`);
  }
}

console.log('=== Stateful WebSocket / Realtime Toy ===');
console.log('WhatsApp-style group chat — 2 server instances behind a load balancer\n');

console.log('>>> IMPORTANT: THIS IS A PURE-JS SIMULATION (delivery accounting).');
console.log('>>> Tadka Day 6 SSE is server→client only; chat needs bidirectional WS + backplane.');
console.log('>>> For real WebSocket servers + Redis pub/sub:');
console.log('>>>   1. docker compose up -d redis');
console.log('>>>   2. npm install');
console.log('>>>   3. node real-chat.js --mode=break   (then --mode=fix)');
console.log('>>> See RUN-AND-TEST.md.\n');

buildTopology();

if (mode === 'break') {
  console.log('--- BREAK: In-memory WebSocket rooms (no backplane) ---\n');
  printResult(simulateDelivery(buildTopology(), false));
  console.log('\nWhy this breaks:');
  console.log('- LB spreads users across instances; room state is local.');
  console.log('- Messages never reach clients on the other instance — "ghost chat".');
  console.log('- Adding a 3rd instance makes it worse (~67% loss with even split).');
  console.log('- SSE + single publisher (Tadka order tracking) avoids this; chat does not.');

} else if (mode === 'fix') {
  console.log('--- FIX: Redis pub/sub backplane ---\n');
  printResult(simulateDelivery(buildTopology(), true));
  console.log('\nWhy this works:');
  console.log('- Sender publishes to Redis channel `room:{id}`; all instances subscribe.');
  console.log('- Each instance fans out only to its local sockets — same pattern as Tadka SSE bus.');
  console.log('- Presence/ordering are separate concerns; backplane fixes cross-instance delivery.');

} else {
  console.log('Usage:');
  console.log('  node index.js --mode=break');
  console.log('  node index.js --mode=fix');
  process.exit(1);
}

console.log('\nDelivery rate is the headline — compare break (~50%) vs fix (100%).');