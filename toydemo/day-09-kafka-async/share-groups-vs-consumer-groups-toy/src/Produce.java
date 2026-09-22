import org.apache.kafka.clients.producer.KafkaProducer;
import org.apache.kafka.clients.producer.ProducerConfig;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.serialization.StringSerializer;

import java.util.Properties;

// Publishes MESSAGE_COUNT records to TOPIC with no key, so the default partitioner
// spreads them across all partitions. Run once; both benchmarks re-read the same
// batch from "earliest" using a fresh group id each time.
public class Produce {
    static final String TOPIC = "share-demo-topic";
    static final int MESSAGE_COUNT = 120;
    static final int PARTITIONS = 3;

    public static void main(String[] args) throws Exception {
        Properties props = new Properties();
        props.put(ProducerConfig.BOOTSTRAP_SERVERS_CONFIG, "localhost:9095");
        props.put(ProducerConfig.KEY_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        props.put(ProducerConfig.VALUE_SERIALIZER_CLASS_CONFIG, StringSerializer.class.getName());
        props.put(ProducerConfig.ACKS_CONFIG, "all");

        // Explicit round-robin partition, not a null key: Kafka's default sticky
        // partitioner batches a fast, keyless send loop onto ONE partition at a time
        // (that's what makes batching efficient), so a tight loop like this one can
        // land almost everything on a single partition instead of spreading evenly.
        // Confirmed by actually running this once with a null key: 120/0/0 across the
        // 3 partitions instead of 40/40/40 - caught before write-up, not assumed.
        try (KafkaProducer<String, String> producer = new KafkaProducer<>(props)) {
            for (int i = 0; i < MESSAGE_COUNT; i++) {
                int partition = i % PARTITIONS;
                producer.send(new ProducerRecord<>(TOPIC, partition, null, "order-" + i));
            }
            producer.flush();
        }
        System.out.println("Produced " + MESSAGE_COUNT + " messages to " + TOPIC);
    }
}
