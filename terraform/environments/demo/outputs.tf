output "alb_dns_name" {
  description = "Client-facing entry (compare to local YARP :8080)"
  value       = module.edge.alb_dns_name
}

output "redis_endpoint" {
  value = module.cache.redis_endpoint
}

output "kafka_bootstrap" {
  value = module.msk.bootstrap_brokers
}

output "ordering_db" {
  value = module.monolith.db_endpoint
}

output "cost_note" {
  value = "Modeled ~₹20-60k/month at this shape — see docs/cost-model.md. This is not a bill."
}