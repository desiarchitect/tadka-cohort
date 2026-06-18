# Shared data primitives — subnet groups, parameter families. Services own their RDS via module.service.

variable "name_prefix" { type = string }

output "note" {
  value = "Per-service RDS lives in module.service (database-per-service, ADR-026). Use this module for shared secrets/parameter store when you extend the stack."
}