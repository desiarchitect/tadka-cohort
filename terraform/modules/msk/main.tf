# MSK (managed Kafka). OPTIONAL: only created when environments/demo sets enable_msk = true (default false).
#
# Why off by default: 2 x kafka.t3.small brokers + storage cost more per month than the rest of this stack's
# compute combined (docs/cost-model.md), for a workload of ~1.2 orders/s. The default is a single
# self-hosted KRaft broker as an ECS service (same image as docker-compose), the same call the Azure live
# deploy makes (ADR-064). Switch to MSK when you need broker replication, managed patching and an SLA:
# i.e. when losing the broker's disk would lose events that the Outbox can no longer replay.

variable "name_prefix" { type = string }
variable "private_subnets" { type = list(string) }
variable "security_group_id" { type = string }

resource "aws_msk_cluster" "this" {
  cluster_name           = "${var.name_prefix}-kafka"
  kafka_version          = "3.6.0"
  number_of_broker_nodes = 2

  broker_node_group_info {
    instance_type   = "kafka.t3.small"
    client_subnets  = var.private_subnets
    security_groups = [var.security_group_id]
    storage_info {
      ebs_storage_info { volume_size = 100 }
    }
  }

  # The apps speak PLAINTEXT today (no SASL/TLS client config), so allow it inside the VPC.
  encryption_info {
    encryption_in_transit {
      client_broker = "TLS_PLAINTEXT"
      in_cluster    = true
    }
  }
}

output "bootstrap_brokers" {
  value = aws_msk_cluster.this.bootstrap_brokers
}
