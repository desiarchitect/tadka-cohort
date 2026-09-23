# Kafka: single-node KRaft broker as a container app (ADR-064).
#
# Why not Event Hubs (the plan's first choice): Tadka uses 14 topics on main (8 main + 6 DLQs), and Event
# Hubs Standard allows 10 event hubs per namespace. Premium/Dedicated fixes that at many times the price.
# So this mirrors docker-compose's `kafka` service: same image, same KRaft combined mode, auto-create topics,
# no persistent volume (the session is thrown away at the end, and the Outbox makes Kafka replayable).
#
# Clients reach it at kafka:9092 through the environment's internal TCP load balancer, and the broker
# advertises exactly that address.

resource "azurerm_container_app" "kafka" {
  name                         = "kafka"
  resource_group_name          = azurerm_resource_group.session.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  template {
    min_replicas = 1
    max_replicas = 1 # one broker; scaling this app would NOT make a cluster

    container {
      name   = "kafka"
      image  = "docker.io/apache/kafka:3.8.0"
      cpu    = 1
      memory = "2Gi"

      env {
        name  = "KAFKA_NODE_ID"
        value = "1"
      }
      env {
        name  = "KAFKA_PROCESS_ROLES"
        value = "broker,controller"
      }
      env {
        name  = "KAFKA_CONTROLLER_QUORUM_VOTERS"
        value = "1@localhost:9093"
      }
      env {
        name  = "KAFKA_LISTENERS"
        value = "CONTROLLER://0.0.0.0:9093,PLAINTEXT://0.0.0.0:9092"
      }
      env {
        name  = "KAFKA_ADVERTISED_LISTENERS"
        value = "PLAINTEXT://kafka:9092"
      }
      env {
        name  = "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"
        value = "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT"
      }
      env {
        name  = "KAFKA_CONTROLLER_LISTENER_NAMES"
        value = "CONTROLLER"
      }
      env {
        name  = "KAFKA_INTER_BROKER_LISTENER_NAME"
        value = "PLAINTEXT"
      }
      env {
        name  = "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR"
        value = "1"
      }
      env {
        name  = "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR"
        value = "1"
      }
      env {
        name  = "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR"
        value = "1"
      }
      env {
        name  = "KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS"
        value = "0"
      }
      env {
        name  = "KAFKA_AUTO_CREATE_TOPICS_ENABLE"
        value = "true"
      }
      env {
        name  = "KAFKA_HEAP_OPTS"
        value = "-Xms768m -Xmx768m"
      }

      # Optional (var.kafka_consumer_scaling): every AUTO-CREATED topic gets 3 partitions, so order-placed
      # can feed 3 Payment consumers. Chosen over a startup `kafka-topics.sh --create` step because topics
      # are auto-created by the first producer: a create script races the Outbox relay, and if the relay
      # wins, --if-not-exists silently leaves order-placed at 1 partition. A broker default has no race.
      # Side effect: all topics (DLQs too) get 3 partitions. Safe: every producer keys by order id, so
      # per-order ordering holds, and the other consumers (1 replica each) simply read all 3.
      dynamic "env" {
        for_each = var.kafka_consumer_scaling ? [1] : []
        content {
          name  = "KAFKA_NUM_PARTITIONS"
          value = "3"
        }
      }
    }
  }

  ingress {
    external_enabled = false
    transport        = "tcp"
    target_port      = 9092
    exposed_port     = 9092
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}
