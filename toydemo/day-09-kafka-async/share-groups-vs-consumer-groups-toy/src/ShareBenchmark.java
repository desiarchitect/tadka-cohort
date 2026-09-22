import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.ConsumerRecords;
import org.apache.kafka.clients.consumer.KafkaShareConsumer;
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

// Share group (KIP-932, GA since Kafka 4.2): the SAME NUM_CONSUMERS threads, the
// SAME 3-partition topic, the SAME simulated per-message work - only the group
// TYPE changes (KafkaShareConsumer instead of KafkaConsumer). The broker tracks
// per-record acquisition locks instead of per-partition ownership, so all
// NUM_CONSUMERS threads can pull work concurrently, not just one per partition.
//
// Messages are STREAMED IN (one every STREAM_GAP_MS, started only after every
// consumer is already subscribed and past the barrier), not pre-loaded as a
// static backlog. This matters: a single poll() call has no built-in per-request
// cap (client-side max.poll.records is silently ignored by KafkaShareConsumer -
// confirmed by running it capped and seeing one consumer still claim an entire
// 40-message backlog in one poll()). Against a backlog that already exists when
// polling starts, whichever consumer's poll() lands first can claim everything
// available in one shot - not a fundamental share-group property, just what
// happens when N pollers race a pre-existing pile with no per-request cap. A
// live stream is also the realistic case share groups are built for (queue
// semantics, like SQS/RabbitMQ), so this is the fair and honest way to measure it.
public class ShareBenchmark {
    public static void main(String[] args) throws Exception {
        int numConsumers = args.length > 0 ? Integer.parseInt(args[0]) : 6;
        int workMs = args.length > 1 ? Integer.parseInt(args[1]) : 100;
        // Fixed, predictable group id (not timestamped) so it can be pre-configured via:
        //   kafka-configs.sh --entity-type groups --entity-name share-demo-group \
        //     --alter --add-config share.auto.offset.reset=earliest
        // share.auto.offset.reset is a per-GROUP dynamic broker config, not a client
        // Properties key and not a broker-wide static default - confirmed by trying
        // both of those first and getting 0/120 processed before finding this one
        // actually works. See the README's "what this toy skips" section.
        String groupId = args.length > 2 ? args[2] : "share-demo-group";
        int streamGapMs = args.length > 3 ? Integer.parseInt(args[3]) : 40;

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
                // Implicit ack mode (the default): poll() auto-acknowledges the previous
                // batch on the next call. Tried explicit per-record ACCEPT+commitSync
                // instead (matching production practice) and it made things much worse
                // under 6-way concurrency - heavy contention on the share-coordinator,
                // throughput collapsed to a fraction of implicit mode's. Kept implicit
                // for this toy and documented the trade-off honestly in the README
                // rather than silently picking whichever number looked best.

                try (KafkaShareConsumer<String, String> consumer = new KafkaShareConsumer<>(props)) {
                    consumer.subscribe(Collections.singletonList(Produce.TOPIC));

                    startBarrier.await(); // wait for every thread to reach "subscribed, not yet polling"

                    long lastRecordAt = System.currentTimeMillis();
                    while (totalProcessed.get() < Produce.MESSAGE_COUNT
                            && System.currentTimeMillis() - lastRecordAt < 12000) {
                        ConsumerRecords<String, String> records = consumer.poll(Duration.ofMillis(300));
                        for (ConsumerRecord<String, String> record : records) {
                            Thread.sleep(workMs); // simulated per-message work
                            perConsumerCounts.get(consumerIndex).incrementAndGet();
                            totalProcessed.incrementAndGet();
                            lastRecordAt = System.currentTimeMillis();
                        }
                    }
                } catch (InterruptedException | BrokenBarrierException ignored) {
                } catch (Exception e) {
                    System.out.println("consumer-" + consumerIndex + " FAILED: " + e);
                }
            });
        }

        startBarrier.await(); // every consumer is now subscribed and about to poll
        long start = System.currentTimeMillis();

        // Stream the messages in now that every consumer is already waiting.
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

        System.out.println("\n=== SHARE GROUP, KIP-932 (group.id=" + groupId + ") ===");
        System.out.println("Consumers started: " + numConsumers + "  |  Topic partitions: 3 (fixed, same topic as classic run)");
        System.out.println("Messages processed: " + totalProcessed.get() + " / " + Produce.MESSAGE_COUNT);
        for (int i = 0; i < numConsumers; i++) {
            System.out.println("  consumer-" + i + " processed: " + perConsumerCounts.get(i).get());
        }
        long idleCount = perConsumerCounts.stream().filter(c -> c.get() == 0).count();
        System.out.println("Idle consumers (got zero records): " + idleCount + " / " + numConsumers);
        System.out.println("Elapsed: " + elapsed + " ms");
    }
}
