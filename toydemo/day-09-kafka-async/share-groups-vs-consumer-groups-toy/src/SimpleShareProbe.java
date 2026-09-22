import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.ConsumerRecords;
import org.apache.kafka.clients.consumer.KafkaShareConsumer;
import org.apache.kafka.common.serialization.StringDeserializer;

import java.time.Duration;
import java.util.Collections;
import java.util.Properties;

// Diagnostic probe: does a single Kafka ShareConsumer actually receive records
// from a topic under a pre-configured share group? No threads, no barrier, no
// multi-consumer contention - just the simplest possible path, to isolate
// whether the broker/group setup itself works before adding concurrency back.
public class SimpleShareProbe {
    public static void main(String[] args) throws Exception {
        Properties props = new Properties();
        props.put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, "localhost:9095");
        props.put(ConsumerConfig.GROUP_ID_CONFIG, "single-part-share");
        props.put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());
        props.put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, StringDeserializer.class.getName());

        try (KafkaShareConsumer<String, String> consumer = new KafkaShareConsumer<>(props)) {
            consumer.subscribe(Collections.singletonList("single-partition-topic"));
            for (int i = 0; i < 10; i++) {
                ConsumerRecords<String, String> records = consumer.poll(Duration.ofMillis(2000));
                System.out.println("poll " + i + ": got " + records.count() + " records");
                for (ConsumerRecord<String, String> r : records) {
                    System.out.println("   " + r.value());
                }
            }
        }
    }
}
