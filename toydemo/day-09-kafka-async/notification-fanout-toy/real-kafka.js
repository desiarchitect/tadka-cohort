#!/usr/bin/env node
/**
 * Real Kafka fan-out demo — one promo event, idempotent consumer.
 *
 * Prerequisites: docker compose up -d kafka
 *
 * Usage: node real-kafka.js --mode=fix
 */

const { Kafka, logLevel } = require('kafkajs');

const TOPIC = 'toydemo.promo-fanout';
const GROUP = 'toydemo-promo-workers';
const USERS = parseInt(process.env.USERS || '500', 10);
const BROKER = process.env.KAFKA_BROKER || 'localhost:9092';

const kafka = new Kafka({
  clientId: 'notification-fanout-toy',
  brokers: [BROKER],
  logLevel: logLevel.ERROR
});

const inbox = new Set();
let sent = 0;
let deduped = 0;

async function ensureTopic(admin) {
  const topics = await admin.listTopics();
  if (!topics.includes(TOPIC)) {
    await admin.createTopics({ topics: [{ topic: TOPIC, numPartitions: 1, replicationFactor: 1 }] });
  }
}

async function runProducer(campaignId) {
  const producer = kafka.producer();
  await producer.connect();
  const userIds = Array.from({ length: USERS }, (_, i) => `user-${i + 1}`);
  await producer.send({
    topic: TOPIC,
    messages: [{
      key: campaignId,
      value: JSON.stringify({ campaignId, userIds })
    }]
  });
  await producer.disconnect();
  console.log(`Produced 1 promo event for ${USERS} users (campaign ${campaignId})`);
}

async function runConsumer(campaignId, redeliverOnce) {
  const consumer = kafka.consumer({ groupId: GROUP });
  await consumer.connect();
  await consumer.subscribe({ topic: TOPIC, fromBeginning: true });

  await consumer.run({
    eachMessage: async ({ message }) => {
      const payload = JSON.parse(message.value.toString());
      if (payload.campaignId !== campaignId) return;

      for (const userId of payload.userIds) {
        const dedupeKey = `${payload.campaignId}:${userId}`;
        if (inbox.has(dedupeKey)) {
          deduped += 1;
          continue;
        }
        inbox.add(dedupeKey);
        sent += 1;
      }
    }
  });

  await new Promise(r => setTimeout(r, 2000));

  if (redeliverOnce) {
    // Simulate at-least-once: re-run consumer path on same payload
    const payload = { campaignId, userIds: Array.from({ length: USERS }, (_, i) => `user-${i + 1}`) };
    for (const userId of payload.userIds) {
      const dedupeKey = `${payload.campaignId}:${userId}`;
      if (inbox.has(dedupeKey)) {
        deduped += 1;
        continue;
      }
      inbox.add(dedupeKey);
      sent += 1;
    }
  }

  await consumer.disconnect();
}

async function main() {
  console.log('=== Notification Fan-Out Toy — Real Kafka ===\n');
  console.log(`Broker: ${BROKER}`);
  console.log('Prerequisite: docker compose up -d kafka\n');

  const admin = kafka.admin();
  await admin.connect();
  await ensureTopic(admin);
  await admin.deleteGroups([GROUP]).catch(() => {});
  await admin.disconnect();

  const campaignId = `biryani-50-${Date.now()}`;
  const start = Date.now();

  await runProducer(campaignId);
  await runConsumer(campaignId, true);

  console.log('\nResults:');
  console.log(`Unique pushes recorded: ${sent}`);
  console.log(`Inbox deduped on redelivery: ${deduped}`);
  console.log(`Wall time: ${Date.now() - start}ms`);
  console.log('\nHeadline: 1 Kafka message fans out; redelivery does not inflate unique sends.');
}

main().catch(err => {
  console.error('Error:', err.message || err);
  console.error('Is Kafka up?  docker compose up -d kafka');
  process.exit(1);
});