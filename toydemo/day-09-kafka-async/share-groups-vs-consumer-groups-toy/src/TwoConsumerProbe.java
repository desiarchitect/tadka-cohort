import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.ConsumerRecords;
import org.apache.kafka.clients.consumer.KafkaShareConsumer;
import org.apache.kafka.common.serialization.StringDeserializer;

import java.time.Duration;
import java.util.Collections;
import java.util.Properties;
import java.util.concurrent.CountDownLatch;

// Simplest possible multi-consumer probe: exactly 2 threads, 1 partition, small
// poll batches, verbose per-poll logging. If even this can't show interleaved
// delivery across both consumers, the issue is structural (broker/version), not
// a benchmark-harness artifact from ClassicBenchmark/ShareBenchmark's complexity.
public class TwoConsumerProbe {
    public static void main(String[] args) throws Exception {
        CountDownLatch startLatch = new CountDownLatch(2);
        for (int i = 0; i < 2; i++) {
            final int idx = i;
            new Thread(() -> {
                Properties props = new Properties();
                props.put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, "localhost:9095");
                props.put(ConsumerConfig.GROUP_ID_CONFIG, "single-part-share");
                props.put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
                props.put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
                props.put(ConsumerConfig.MAX_POLL_RECORDS_CONFIG, "3");
                props.put(ConsumerConfig.CLIENT_ID_CONFIG, "probe-client-" + idx);
                try (KafkaShareConsumer<String, String> consumer = new KafkaShareConsumer<>(props)) {
                    consumer.subscribe(Collections.singletonList("single-partition-topic"));
                    startLatch.countDown();
                    startLatch.await();
                    for (int p = 0; p < 15; p++) {
                        ConsumerRecords<String, String> records = consumer.poll(Duration.ofMillis(1000));
                        System.out.println("thread-" + idx + " poll " + p + ": got " + records.count());
                        for (ConsumerRecord<String, String> r : records) {
                            System.out.println("   thread-" + idx + " -> " + r.value());
                        }
                        Thread.sleep(50);
                    }
                } catch (Exception e) {
                    System.out.println("thread-" + idx + " FAILED: " + e);
                    e.printStackTrace();
                }
            }).start();
        }
        Thread.sleep(20000);
    }
}
