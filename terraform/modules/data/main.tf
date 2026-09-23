# Placeholder kept for layout compatibility. Per-service RDS lives in module.service (database-per-service,
# ADR-026); shared secrets live in module.platform (SSM Parameter Store). Not referenced by environments/demo.

variable "name_prefix" { type = string }

output "note" {
  value = "Per-service RDS: module.service. Shared secrets: module.platform. This module is intentionally empty."
}
