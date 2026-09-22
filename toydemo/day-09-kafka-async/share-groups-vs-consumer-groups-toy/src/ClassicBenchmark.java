import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.ConsumerRecords;
import org.apache.kafka.clients.consumer.KafkaConsumer;
import org.apache.kafka.clients.producer.KafkaProducer;
import org.apache.kafka.clients.producer.ProducerConfig;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.serialization.StringDeserializer;
import org.apache.kafka.common.serialization.StringSerializer;

import java.time.Duration;
import java.util.Collections;
import java.util.List;
import java.util.Properties;
import java.util.concurrent.BrokenBarrierException;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

// Classic consumer group: NUM_CONSUMERS threads join the SAME group.id.
// Kafka's classic protocol assigns each partition to exactly one consumer, so
// with a 3-partition topic, at most 3 of the NUM_CONSUMERS threads ever get work -
// the rest poll forever and receive nothing. This is the textbook "one consumer
// per partition" limit that KIP-932 (share groups) breaks. See ShareBenchmark.java
// for the identical workload run through a share group instead - same topic, same
// producer, same streaming pace, same simulated work, only the consumer type and
// group protocol differ.
//
// A SETTLE phase runs first: every thread joins the group and lets rebalancing
// finish, then rewinds to the start of whatever it was assigned (seekToBeginning)
// before the timed run begins. Without this, whichever consumer's JoinGroup wins
// the initial race can grab every partition before the slower threads finish
// joining at all - a race in THIS harness, not a real Kafka property, caught by
// actually running it before writing this up. Messages are then STREAMED IN
// (matching ShareBenchmark's methodology exactly) rather than pre-loaded, and
// each processed batch is committed immediately so a late-joining straggler that
// triggers a mid-run rebalance resumes from the last committed offset instead of
// re-reading a whole partition from earliest and double-counting - also a real
// bug this toy hit on the way to this final version, not a hypothetical one.
public class ClassicBenchmark {
    public static void main(String[] args) throws Exception {
        int numConsumers = args.length > 0 ? Integer.parseInt(args[0]) : 6;
        int workMs = args.length > 1 ? Integer.parseInt(args[1]) : 100;
        int streamGapMs = args.length > 2 ? Integer.parseInt(args[2]) : 40;
        String groupId = "classic-bench-" + System.currentTimeMillis();
        System.out.println("group.id=" + groupId);
        System.out.flush();

        AtomicInteger totalProcessed = new AtomicInteger(0);
        List<AtomicInteger> perConsumerCounts = new CopyOnWriteArrayList<>();
        for (int i = 0; i < numConsumers; i++) perConsumerCounts.add(new AtomicInteger(0));

        CyclicBarrier startBarrier = new CyclicBarrier(numConsumers + 1);
        ExecutorService pool = Executors.newFixedThreadPool(numConsumers);

        for (int i = 0; i < numConsumers; i++) {
            final int consumerIndex = i;
            pool.submit(() -> {
                Properties props = new Properties();
                props.put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, "localhost:9095");
                props.put(ConsumerConfig.GROUP_ID_CONFIG, groupId);
                props.put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
                props.put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
                props.put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "earliest");
                props.put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, "false");

                try (KafkaConsumer<String, String> consumer = new KafkaConsumer<>(props)) {
                    consumer.subscribe(Collections.singletonList(Produce.TOPIC));

                    // SETTLE: keep polling (discarding results) until the group has had
                    // time to fully rebalance across all numConsumers joiners. The topic
                    // is empty at this point (producer hasn't started yet), so there's
                    // nothing to discard - this phase exists purely to let rebalancing
                    // finish before the timed run begins.
                    long settleUntil = System.currentTimeMillis() + 5000;
                    while (System.currentTimeMillis() < settleUntil) {
                        consumer.poll(Duration.ofMillis(200));
                    }
                    if (!consumer.assignment().isEmpty()) {
                        consumer.seekToBeginning(consumer.assignment());
                    }

                    startBarrier.await(); // released once every thread + main has settled

                    long lastRecordAt = System.currentTimeMillis();
                    while (totalProcessed.get() < Produce.MESSAGE_COUNT
                            && System.currentTimeMillis() - lastRecordAt < 8000) {
                        ConsumerRecords<String, String> records = consumer.poll(Duration.ofMillis(300));
                        for (ConsumerRecord<String, String> record : records) {
                            Thread.sleep(workMs); // simulated per-message work
                            perConsumerCounts.get(consumerIndex).incrementAndGet();
                            totalProcessed.incrementAndGet();
                            lastRecordAt = System.currentTimeMillis();
                        }
                        // Commit after every batch: if a late joiner triggers a mid-run
                        // rebalance, the new partition owner resumes from here instead of
                        // re-reading from earliest and double-counting already-processed
                        // records.
                        if (!records.isEmpty()) consumer.commitSync();
                    }
                } catch (InterruptedException | BrokenBarrierException ignored) {
                } catch (Exception e) {
                    System.out.println("consumer-" + consumerIndex + " FAILED: " + e);
                }
            });
        }

        startBarrier.await(); // main waits for every consumer to finish settling
        long start = System.currentTimeMillis();

        // Stream the messages in now that every consumer has a stable assignment -
        // identical pacing/partitioning to ShareBenchmark for a fair comparison.
        Properties pprops = new Properties();
        pprops.put(ProducerConfig.BOOTSTRAP_SERVERS_CONFIG, "localhost:9095");
        pprops.put(ProducerConfig.KEY_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        pprops.put(ProducerConfig.VALUE_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        try (KafkaProducer<String, String> producer = new KafkaProducer<>(pprops)) {
            for (int i = 0; i < Produce.MESSAGE_COUNT; i++) {
                int partition = i % Produce.PARTITIONS;
                producer.send(new ProducerRecord<>(Produce.TOPIC, partition, null, "order-" + i));
                if (streamGapMs > 0) Thread.sleep(streamGapMs);
            }
            producer.flush();
        }

        pool.shutdown();
        pool.awaitTermination(120, java.util.concurrent.TimeUnit.SECONDS);
        long elapsed = System.currentTimeMillis() - start;

        System.out.println("\n=== CLASSIC CONSUMER GROUP (group.id=" + groupId + ") ===");
        System.out.println("Consumers started: " + numConsumers + "  |  Topic partitions: 3 (fixed)");
        System.out.println("Messages processed: " + totalProcessed.get() + " / " + Produce.MESSAGE_COUNT);
        for (int i = 0; i < numConsumers; i++) {
            System.out.println("  consumer-" + i + " processed: " + perConsumerCounts.get(i).get());
        }
        long idleCount = perConsumerCounts.stream().filter(c -> c.get() == 0).count();
        System.out.println("Idle consumers (got zero partitions): " + idleCount + " / " + numConsumers);
        System.out.println("Elapsed (timed phase only, settle excluded): " + elapsed + " ms");
    }
}
