# MSK (Kafka) — async backbone in AWS (ADR-027). Cost driver: see docs/cost-model.md.

variable "name_prefix" { type = string }
variable "private_subnets" { type = list(string) }

resource "aws_msk_cluster" "this" {
  cluster_name           = "${var.name_prefix}-kafka"
  kafka_version          = "3.6.0"
  number_of_broker_nodes = 2

  broker_node_group_info {
    instance_type   = "kafka.t3.small"
    client_subnets  = var.private_subnets
    storage_info {
      ebs_storage_info { volume_size = 100 }
    }
  }
}

output "bootstrap_brokers" {
  value = aws_msk_cluster.this.bootstrap_brokers
}